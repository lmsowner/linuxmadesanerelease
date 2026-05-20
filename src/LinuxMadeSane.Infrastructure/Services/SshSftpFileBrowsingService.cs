// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SshSftpFileBrowsingService(
    ILogger<SshSftpFileBrowsingService> logger,
    ManagedHostSshConnectionFactory sshConnectionFactory) : ISftpFileBrowsingService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = NormalizePath(path);
        var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
        logger.LogInformation("Listing SFTP items for host {HostId} path {Path}", host.Id, normalizedPath);
        ISftpFile[] sftpItems;
        try
        {
            sftpItems = await RunBlockingRemoteOperationAsync(
                () =>
                {
                    using var client = Connect(host, credentials);
                    var items = client
                        .ListDirectory(normalizedPath)
                        .Where(item => item.Name is not "." and not "..")
                        .ToArray();
                    client.Disconnect();
                    return items;
                },
                cancellationToken);
        }
        catch (SftpPathNotFoundException exception)
        {
            throw new FileAccessPathNotFoundException(
                normalizedPath,
                $"Folder {normalizedPath} does not exist on {host.Name}.",
                exception);
        }

        var metadataByPath = await TryReadRemoteMetadataAsync(host, credentials, normalizedPath, cancellationToken);
        var items = sftpItems
            .Select(item => MapItem(item, metadataByPath))
            .OrderByDescending(item => item.ItemType == SftpItemType.Folder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return items;
    }

    public async Task<FileSearchResponse> SearchAsync(
        ManagedHost host,
        FileSearchRequest request,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRequest = NormalizeSearchRequest(request);
        var credentials = await ResolveCredentialsAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            cancellationToken);

        var result = await RunBlockingRemoteOperationAsync(
            () =>
            {
                using var client = ConnectShell(host, credentials);
                using var command = client.CreateCommand(BuildPythonFileSearchCommand(normalizedRequest));
                command.CommandTimeout = TimeSpan.FromMinutes(10);
                using var cancellationRegistration = cancellationToken.Register(
                    static state =>
                    {
                        try
                        {
                            ((SshClient)state!).Disconnect();
                        }
                        catch
                        {
                            // Ignore disconnect failures during cancellation.
                        }
                    },
                    client);

                logger.LogInformation("Executing remote file search for host {HostId} path {Path}", host.Id, normalizedRequest.RootPath);
                var asyncResult = command.BeginExecute();
                var errorTask = ReadRemoteSearchErrorStreamAsync(command, progress, cancellationToken);
                var output = command.EndExecute(asyncResult);
                var error = errorTask.GetAwaiter().GetResult();
                var exitCode = command.ExitStatus ?? -1;

                client.Disconnect();
                return (output, error, exitCode);
            },
            cancellationToken);

        if (result.exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.error)
                ? string.IsNullOrWhiteSpace(result.output)
                    ? $"Remote file search failed on {host.Name} with exit code {result.exitCode}."
                    : result.output.Trim()
                : result.error.Trim();
            throw new InvalidOperationException(detail);
        }

        var payload = string.IsNullOrWhiteSpace(result.output)
            ? new RemoteFileSearchPayload([], false)
            : JsonSerializer.Deserialize<RemoteFileSearchPayload>(result.output)
              ?? new RemoteFileSearchPayload([], false);

        var searchResults = payload.Results
            .Select(result => new FileSearchMatch(
                result.Name,
                result.FullPath,
                result.ParentPath,
                result.ItemType,
                result.SizeBytes,
                result.LastModifiedUtc,
                result.MatchedContents,
                result.LinkTarget))
            .OrderBy(result => result.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FileSearchResponse(normalizedRequest.RootPath, searchResults, payload.LimitReached);
    }

    public async Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = NormalizePath(path);
        var safeMaxBytes = Math.Clamp(maxBytes, 1, 1_048_576);
        var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
        using var client = Connect(host, credentials);

        var attributes = client.GetAttributes(normalizedPath);
        if (attributes.IsDirectory)
        {
            throw new InvalidOperationException("The requested path is a directory, not a file.");
        }

        using var stream = client.OpenRead(normalizedPath);
        var buffer = new byte[safeMaxBytes];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);
        var isTruncated = attributes.Size > bytesRead;
        var decoded = TextFileEncoding.Decode(buffer.AsSpan(0, bytesRead));

        client.Disconnect();

        return new SftpFileContent(
            normalizedPath,
            decoded.Content,
            attributes.Size,
            attributes.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(attributes.LastWriteTimeUtc, TimeSpan.Zero),
            isTruncated,
            decoded.EncodingName);
    }

    public async Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = NormalizePath(path);
        var safeMaxBytes = Math.Clamp(maxBytes, 1, 67_108_864);
        var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
        using var client = Connect(host, credentials);

        var attributes = client.GetAttributes(normalizedPath);
        if (attributes.IsDirectory)
        {
            throw new InvalidOperationException("The requested path is a directory, not a file.");
        }

        using var stream = client.OpenRead(normalizedPath);
        var buffer = new byte[safeMaxBytes];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);
        var contentBytes = new byte[bytesRead];
        Buffer.BlockCopy(buffer, 0, contentBytes, 0, bytesRead);

        client.Disconnect();

        return new SftpBinaryFileContent(
            normalizedPath,
            contentBytes,
            attributes.Size,
            attributes.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(attributes.LastWriteTimeUtc, TimeSpan.Zero),
            attributes.Size > bytesRead);
    }

    public async Task DownloadFileAsync(
        ManagedHost host,
        string sourcePath,
        string localDestinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = NormalizePath(sourcePath);
        var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
        var destinationDirectory = Path.GetDirectoryName(localDestinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        await RunBlockingRemoteOperationAsync(
            () =>
            {
                using var client = Connect(host, credentials);
                using var sourceStream = client.OpenRead(normalizedSourcePath);
                using var destinationStream = File.Create(localDestinationPath);
                CopyStreamWithProgress(sourceStream, destinationStream, progress, cancellationToken);
                destinationStream.Flush();
                client.Disconnect();
                return 0;
            },
            cancellationToken);
    }

    public async Task UploadFileAsync(
        ManagedHost host,
        string localSourcePath,
        string destinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(localSourcePath))
        {
            throw new InvalidOperationException($"{localSourcePath} was not found on the local machine.");
        }

        var normalizedDestinationPath = NormalizePath(destinationPath);
        var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);

        await RunBlockingRemoteOperationAsync(
            () =>
            {
                using var client = Connect(host, credentials);
                EnsureDirectoryExists(client, GetDirectoryName(normalizedDestinationPath));
                using var sourceStream = File.OpenRead(localSourcePath);
                using var destinationStream = client.OpenWrite(normalizedDestinationPath);
                destinationStream.SetLength(0);
                CopyStreamWithProgress(sourceStream, destinationStream, progress, cancellationToken);
                destinationStream.Flush();
                client.Disconnect();
                return 0;
            },
            cancellationToken);
    }

    public async Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        bool createDirectories,
        string? encodingName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = NormalizePath(path);
        var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
        using var client = Connect(host, credentials);

        if (createDirectories)
        {
            EnsureDirectoryExists(client, GetDirectoryName(normalizedPath));
        }

        var bytes = TextFileEncoding.Encode(content, encodingName);
        using (var stream = client.OpenWrite(normalizedPath))
        {
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        client.Disconnect();

        return new SftpWriteResult(
            normalizedPath,
            bytes.Length,
            DateTimeOffset.UtcNow);
    }

    public async Task<string> CreateDirectoryAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = NormalizePath(path);
        EnsureMutablePath(normalizedPath);
        var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);

        await RunBlockingRemoteOperationAsync(
            () =>
            {
                using var client = Connect(host, credentials);
                if (client.Exists(normalizedPath))
                {
                    var attributes = client.GetAttributes(normalizedPath);
                    if (!attributes.IsDirectory)
                    {
                        throw new InvalidOperationException($"{normalizedPath} already exists and is not a folder.");
                    }

                    client.Disconnect();
                    return 0;
                }

                EnsureDirectoryExists(client, GetDirectoryName(normalizedPath));
                client.CreateDirectory(normalizedPath);
                client.Disconnect();
                return 0;
            },
            cancellationToken);

        return normalizedPath;
    }

    public async Task DeleteAsync(
        ManagedHost host,
        string path,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = NormalizePath(path);
        EnsureMutablePath(normalizedPath);
        var quotedPath = QuoteShellArgument(normalizedPath);
        var command = recursive
            ? $"rm -rf -- {quotedPath}"
            : $"if [ -d {quotedPath} ] && [ ! -L {quotedPath} ]; then rmdir -- {quotedPath}; else rm -f -- {quotedPath}; fi";

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            command,
            cancellationToken);
    }

    public async Task<string> CopyAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default,
        IProgress<FileTransferProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = NormalizePath(sourcePath);
        var normalizedDestinationPath = NormalizePath(destinationPath);
        EnsureDistinctPaths(normalizedSourcePath, normalizedDestinationPath);
        EnsureDestinationNotWithinSource(normalizedSourcePath, normalizedDestinationPath);

        if (progress is not null)
        {
            var credentials = await ResolveCredentialsAsync(host, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
            if (await TryCopyRemoteFileWithProgressAsync(
                host,
                credentials,
                normalizedSourcePath,
                normalizedDestinationPath,
                progress,
                cancellationToken))
            {
                return normalizedDestinationPath;
            }
        }

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            $"cp -a -- {QuoteShellArgument(normalizedSourcePath)} {QuoteShellArgument(normalizedDestinationPath)}",
            cancellationToken);

        return normalizedDestinationPath;
    }

    public async Task<string> MoveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = NormalizePath(sourcePath);
        var normalizedDestinationPath = NormalizePath(destinationPath);
        EnsureMutablePath(normalizedSourcePath);
        EnsureDistinctPaths(normalizedSourcePath, normalizedDestinationPath);
        EnsureDestinationNotWithinSource(normalizedSourcePath, normalizedDestinationPath);

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            $"mv -- {QuoteShellArgument(normalizedSourcePath)} {QuoteShellArgument(normalizedDestinationPath)}",
            cancellationToken);

        return normalizedDestinationPath;
    }

    public async Task<string> CreateZipAsync(
        ManagedHost host,
        string sourcePath,
        string destinationZipPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = NormalizePath(sourcePath);
        var normalizedDestinationZipPath = NormalizePath(destinationZipPath);
        var command = BuildPythonArchiveCommand(normalizedSourcePath, normalizedDestinationZipPath);

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            command,
            cancellationToken);

        return normalizedDestinationZipPath;
    }

    public async Task<string> CreateArchiveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        if (format == ArchiveFormat.Zip)
        {
            return await CreateZipAsync(host, sourcePath, destinationArchivePath, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = NormalizePath(sourcePath);
        var normalizedDestinationArchivePath = NormalizePath(destinationArchivePath);
        if (format == ArchiveFormat.Gzip && !normalizedDestinationArchivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            format = ArchiveFormat.TarGzip;
        }

        var command = format switch
        {
            ArchiveFormat.Gzip => BuildRemoteGzipArchiveCommand(normalizedSourcePath, normalizedDestinationArchivePath),
            ArchiveFormat.TarGzip => BuildRemoteTarGzipArchiveCommand(normalizedSourcePath, normalizedDestinationArchivePath),
            ArchiveFormat.SevenZip => BuildRemoteSevenZipCreateCommand(normalizedSourcePath, normalizedDestinationArchivePath),
            ArchiveFormat.Rar => BuildRemoteRarCreateCommand(normalizedSourcePath, normalizedDestinationArchivePath),
            _ => throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} compression is not supported.")
        };

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            command,
            cancellationToken);

        return normalizedDestinationArchivePath;
    }

    public async Task<string> ExtractZipAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArchivePath = NormalizePath(archivePath);
        var normalizedDestinationDirectoryPath = NormalizePath(destinationDirectoryPath);
        EnsureMutablePath(normalizedDestinationDirectoryPath);
        var command = BuildPythonExtractCommand(normalizedArchivePath, normalizedDestinationDirectoryPath);

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            command,
            cancellationToken);

        return normalizedDestinationDirectoryPath;
    }

    public async Task<string> ExtractArchiveAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        if (format == ArchiveFormat.Zip)
        {
            return await ExtractZipAsync(host, archivePath, destinationDirectoryPath, username, password, privateKey, privateKeyPassphrase, preferStoredCredentials, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArchivePath = NormalizePath(archivePath);
        var normalizedDestinationDirectoryPath = NormalizePath(destinationDirectoryPath);
        EnsureMutablePath(normalizedDestinationDirectoryPath);
        var command = format switch
        {
            ArchiveFormat.Gzip => BuildRemoteGzipExtractCommand(normalizedArchivePath, normalizedDestinationDirectoryPath),
            ArchiveFormat.TarGzip => BuildRemoteTarGzipExtractCommand(normalizedArchivePath, normalizedDestinationDirectoryPath),
            ArchiveFormat.SevenZip or ArchiveFormat.Rar => BuildRemoteSevenZipExtractCommand(normalizedArchivePath, normalizedDestinationDirectoryPath, format),
            _ => throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} extraction is not supported.")
        };

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            command,
            cancellationToken);

        return normalizedDestinationDirectoryPath;
    }

    public async Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        ManagedHost host,
        string archivePath,
        ArchiveFormat format,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        int maxEntries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArchivePath = NormalizePath(archivePath);
        var safeMaxEntries = Math.Clamp(maxEntries, 1, 2_000);
        if (format == ArchiveFormat.Gzip)
        {
            return ListRemoteGzipEntries(normalizedArchivePath);
        }

        var command = format switch
        {
            ArchiveFormat.Zip => BuildPythonListZipCommand(normalizedArchivePath, safeMaxEntries),
            ArchiveFormat.TarGzip => BuildRemoteTarGzipListCommand(normalizedArchivePath),
            ArchiveFormat.SevenZip or ArchiveFormat.Rar => BuildRemoteSevenZipListCommand(normalizedArchivePath),
            _ => throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} preview is not supported.")
        };

        var output = await ExecuteShellCommandForOutputAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            command,
            cancellationToken);

        return format switch
        {
            ArchiveFormat.Zip => DeserializeRemoteZipEntries(output),
            ArchiveFormat.TarGzip => ParseSimpleArchiveEntryList(output, safeMaxEntries),
            ArchiveFormat.SevenZip or ArchiveFormat.Rar => ParseSevenZipTechnicalList(output, safeMaxEntries),
            _ => []
        };
    }

    public async Task SetOwnershipAndPermissionsAsync(
        ManagedHost host,
        FileOwnershipPermissionsChangeRequest request,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = NormalizePath(request.Path);
        EnsureMutablePath(normalizedPath);
        var ownershipSpec = BuildOwnershipSpec(request.OwnerName, request.GroupName);
        var permissionsOctal = NormalizePermissionMode(request.PermissionsOctal);

        if (ownershipSpec is null && permissionsOctal is null)
        {
            throw new InvalidOperationException("Provide an owner, group, or octal permissions value before applying changes.");
        }

        var recursive = request.Recursive;
        var command = BuildOwnershipPermissionsCommand(normalizedPath, ownershipSpec, permissionsOctal, recursive);

        await ExecuteShellCommandAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            command,
            cancellationToken);
    }

    private SftpClient Connect(
        ManagedHost host,
        ManagedHostSshCredentials credentials)
    {
        var client = sshConnectionFactory.CreateSftpClient(host, credentials, ConnectTimeout);

        logger.LogInformation("Opening SFTP connection for host {HostId}", host.Id);
        client.Connect();
        return client;
    }

    private SshClient ConnectShell(
        ManagedHost host,
        ManagedHostSshCredentials credentials)
    {
        var client = sshConnectionFactory.CreateSshClient(host, credentials, ConnectTimeout, KeepAliveInterval);

        logger.LogInformation("Opening SSH shell connection for host {HostId}", host.Id);
        client.Connect();
        return client;
    }

    private async Task<ManagedHostSshCredentials> ResolveCredentialsAsync(
        ManagedHost host,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        CancellationToken cancellationToken)
        => await sshConnectionFactory.ResolveCredentialsAsync(
            host,
            new ManagedHostSshCredentialRequest(
                username,
                password,
                privateKey,
                privateKeyPassphrase,
                preferStoredCredentials),
            cancellationToken);

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? "." : path.Trim();

    private static FileSearchRequest NormalizeSearchRequest(FileSearchRequest request)
    {
        var normalizedRootPath = NormalizePath(request.RootPath);
        var safeMaxResults = Math.Clamp(request.MaxResults, 1, 2_000);
        var minimumSizeBytes = request.MinimumSizeBytes;
        var maximumSizeBytes = request.MaximumSizeBytes;

        if (minimumSizeBytes.HasValue && minimumSizeBytes.Value < 0)
        {
            throw new InvalidOperationException("Minimum size must be zero or greater.");
        }

        if (maximumSizeBytes.HasValue && maximumSizeBytes.Value < 0)
        {
            throw new InvalidOperationException("Maximum size must be zero or greater.");
        }

        if (minimumSizeBytes.HasValue && maximumSizeBytes.HasValue && minimumSizeBytes.Value > maximumSizeBytes.Value)
        {
            throw new InvalidOperationException("Minimum size cannot be greater than maximum size.");
        }

        if (request.ModifiedFromUtc.HasValue && request.ModifiedToUtc.HasValue && request.ModifiedFromUtc.Value > request.ModifiedToUtc.Value)
        {
            throw new InvalidOperationException("Modified from must be earlier than modified to.");
        }

        if (request.CreatedFromUtc.HasValue && request.CreatedToUtc.HasValue && request.CreatedFromUtc.Value > request.CreatedToUtc.Value)
        {
            throw new InvalidOperationException("Created from must be earlier than created to.");
        }

        if (request.AccessedFromUtc.HasValue && request.AccessedToUtc.HasValue && request.AccessedFromUtc.Value > request.AccessedToUtc.Value)
        {
            throw new InvalidOperationException("Last accessed from must be earlier than last accessed to.");
        }

        if (!request.IncludeFiles && !request.IncludeFolders)
        {
            throw new InvalidOperationException("Select files, folders, or both before searching.");
        }

        return request with
        {
            RootPath = normalizedRootPath,
            NamePattern = request.NamePattern?.Trim(),
            ContainsText = request.ContainsText?.Trim(),
            MaxResults = safeMaxResults
        };
    }

    private async Task ExecuteShellCommandAsync(
        ManagedHost host,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        string commandText,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveCredentialsAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            cancellationToken);

        var result = await RunBlockingRemoteOperationAsync(
            () =>
            {
                using var client = ConnectShell(host, credentials);
                using var command = client.CreateCommand(commandText);
                command.CommandTimeout = TimeSpan.FromMinutes(5);

                logger.LogInformation("Executing remote file operation for host {HostId}: {CommandText}", host.Id, commandText);
                var output = command.Execute();
                var error = command.Error ?? string.Empty;
                var exitCode = command.ExitStatus ?? -1;

                client.Disconnect();
                return (output, error, exitCode);
            },
            cancellationToken);

        if (result.exitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.error)
            ? string.IsNullOrWhiteSpace(result.output)
                ? $"Remote file operation failed on {host.Name} with exit code {result.exitCode}."
                : result.output.Trim()
            : result.error.Trim();
        throw new InvalidOperationException(detail);
    }

    private async Task<string> ExecuteShellCommandForOutputAsync(
        ManagedHost host,
        string username,
        string? password,
        string? privateKey,
        string? privateKeyPassphrase,
        bool preferStoredCredentials,
        string commandText,
        CancellationToken cancellationToken)
    {
        var credentials = await ResolveCredentialsAsync(
            host,
            username,
            password,
            privateKey,
            privateKeyPassphrase,
            preferStoredCredentials,
            cancellationToken);

        var result = await RunBlockingRemoteOperationAsync(
            () =>
            {
                using var client = ConnectShell(host, credentials);
                using var command = client.CreateCommand(commandText);
                command.CommandTimeout = TimeSpan.FromMinutes(5);

                logger.LogInformation("Executing remote file inspection for host {HostId}: {CommandText}", host.Id, commandText);
                var output = command.Execute();
                var error = command.Error ?? string.Empty;
                var exitCode = command.ExitStatus ?? -1;

                client.Disconnect();
                return (output, error, exitCode);
            },
            cancellationToken);

        if (result.exitCode == 0)
        {
            return result.output;
        }

        var detail = string.IsNullOrWhiteSpace(result.error)
            ? string.IsNullOrWhiteSpace(result.output)
                ? $"Remote file inspection failed on {host.Name} with exit code {result.exitCode}."
                : result.output.Trim()
            : result.error.Trim();
        throw new InvalidOperationException(detail);
    }

    private static string GetDirectoryName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSeparator = normalized.LastIndexOf('/');
        return lastSeparator switch
        {
            < 0 => ".",
            0 => "/",
            _ => normalized[..lastSeparator]
        };
    }

    private static void EnsureDirectoryExists(SftpClient client, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || directoryPath == ".")
        {
            return;
        }

        var segments = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = directoryPath.StartsWith("/", StringComparison.Ordinal) ? "/" : string.Empty;

        foreach (var segment in segments)
        {
            current = string.IsNullOrEmpty(current) || current == "/"
                ? $"{current}{segment}"
                : $"{current}/{segment}";

            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }

    private static void EnsureMutablePath(string path)
    {
        if (path is "/" or ".")
        {
            throw new InvalidOperationException("The remote filesystem root cannot be modified.");
        }
    }

    private static void EnsureDistinctPaths(string sourcePath, string destinationPath)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The source and destination paths are the same.");
        }
    }

    private static void EnsureDestinationNotWithinSource(string sourcePath, string destinationPath)
    {
        if (sourcePath is "/" or "." ||
            !destinationPath.StartsWith(sourcePath.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("A directory cannot be copied or moved into itself.");
    }

    private static string QuoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string? BuildOwnershipSpec(string? ownerName, string? groupName)
    {
        var owner = ownerName?.Trim() ?? string.Empty;
        var group = groupName?.Trim() ?? string.Empty;
        if (owner.Length == 0 && group.Length == 0)
        {
            return null;
        }

        return group.Length == 0
            ? owner
            : $"{owner}:{group}";
    }

    private static string? NormalizePermissionMode(string? permissionsOctal)
    {
        var trimmed = permissionsOctal?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length is < 3 or > 4 || trimmed.Any(character => character is < '0' or > '7'))
        {
            throw new InvalidOperationException("Permissions must be a 3 or 4 digit octal value such as 775 or 2775.");
        }

        return trimmed;
    }

    private static string BuildOwnershipPermissionsCommand(
        string path,
        string? ownershipSpec,
        string? permissionsOctal,
        bool recursive)
    {
        var commands = new List<string>
        {
            "set -e",
            "run_with_optional_sudo() {",
            "  if \"$@\"; then",
            "    return 0",
            "  fi",
            "  status=$?",
            "  if [ \"$(id -u)\" -ne 0 ] && command -v sudo >/dev/null 2>&1; then",
            "    sudo -n \"$@\"",
            "    return $?",
            "  fi",
            "  return \"$status\"",
            "}"
        };

        if (ownershipSpec is not null)
        {
            var recursiveFlag = recursive ? "-R " : string.Empty;
            commands.Add($"run_with_optional_sudo chown {recursiveFlag}-- {QuoteShellArgument(ownershipSpec)} {QuoteShellArgument(path)}");
        }

        if (permissionsOctal is not null)
        {
            var recursiveFlag = recursive ? "-R " : string.Empty;
            commands.Add($"run_with_optional_sudo chmod {recursiveFlag}-- {QuoteShellArgument(permissionsOctal)} {QuoteShellArgument(path)}");
        }

        return string.Join('\n', commands);
    }

    private async Task<IReadOnlyDictionary<string, RemoteFileMetadata>> TryReadRemoteMetadataAsync(
        ManagedHost host,
        ManagedHostSshCredentials credentials,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var result = await RunBlockingRemoteOperationAsync(
                () =>
                {
                    using var client = ConnectShell(host, credentials);
                    using var command = client.CreateCommand(BuildPythonMetadataCommand(path));
                    command.CommandTimeout = TimeSpan.FromSeconds(8);

                    var output = command.Execute();
                    var exitCode = command.ExitStatus ?? -1;
                    var error = command.Error ?? string.Empty;
                    client.Disconnect();
                    return (output, exitCode, error);
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.exitCode != 0 || string.IsNullOrWhiteSpace(result.output))
            {
                logger.LogDebug(
                    "Remote metadata lookup failed for host {HostId} path {Path}: {Error}",
                    host.Id,
                    path,
                    result.error);
                return new Dictionary<string, RemoteFileMetadata>(StringComparer.Ordinal);
            }

            var metadata = JsonSerializer.Deserialize<IReadOnlyList<RemoteFileMetadata>>(result.output) ?? [];
            var metadataByPath = new Dictionary<string, RemoteFileMetadata>(StringComparer.Ordinal);
            foreach (var item in metadata)
            {
                AddRemoteMetadata(metadataByPath, item.Path, item);
                AddRemoteMetadata(metadataByPath, item.Name, item);
            }

            return metadataByPath;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Remote metadata lookup failed for host {HostId} path {Path}", host.Id, path);
            return new Dictionary<string, RemoteFileMetadata>(StringComparer.Ordinal);
        }
    }

    private static Task<T> RunBlockingRemoteOperationAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            },
            cancellationToken);

    private async Task<bool> TryCopyRemoteFileWithProgressAsync(
        ManagedHost host,
        ManagedHostSshCredentials credentials,
        string normalizedSourcePath,
        string normalizedDestinationPath,
        IProgress<FileTransferProgress> progress,
        CancellationToken cancellationToken)
    {
        return await RunBlockingRemoteOperationAsync(
            () =>
            {
                using var client = Connect(host, credentials);
                if (!client.Exists(normalizedSourcePath))
                {
                    throw new InvalidOperationException($"{normalizedSourcePath} was not found on {host.Name}.");
                }

                var attributes = client.GetAttributes(normalizedSourcePath);
                if (attributes.IsDirectory || attributes.IsSymbolicLink)
                {
                    client.Disconnect();
                    return false;
                }

                if (client.Exists(normalizedDestinationPath))
                {
                    throw new InvalidOperationException($"{normalizedDestinationPath} already exists on {host.Name}.");
                }

                EnsureDirectoryExists(client, GetDirectoryName(normalizedDestinationPath));
                using var sourceStream = client.OpenRead(normalizedSourcePath);
                using var destinationStream = client.OpenWrite(normalizedDestinationPath);
                destinationStream.SetLength(0);
                CopyStreamWithProgress(
                    sourceStream,
                    destinationStream,
                    progress,
                    cancellationToken,
                    attributes.Size);
                destinationStream.Flush();
                TryPreserveRemoteFileAttributes(client, normalizedDestinationPath, attributes);
                client.Disconnect();
                return true;
            },
            cancellationToken);
    }

    private static void TryPreserveRemoteFileAttributes(
        SftpClient client,
        string destinationPath,
        SftpFileAttributes attributes)
    {
        try
        {
            client.SetAttributes(destinationPath, attributes);
        }
        catch
        {
            // Best effort only; the data copy has already completed.
        }
    }

    private static void CopyStreamWithProgress(
        Stream sourceStream,
        Stream destinationStream,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken,
        long? totalBytesOverride = null)
    {
        const int bufferSize = 1024 * 128;
        var buffer = new byte[bufferSize];
        long totalBytesTransferred = 0;
        long? totalBytes = totalBytesOverride ?? (sourceStream.CanSeek ? sourceStream.Length : null);

        progress?.Report(new FileTransferProgress(0, totalBytes));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = sourceStream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            destinationStream.Write(buffer, 0, bytesRead);
            totalBytesTransferred += bytesRead;
            progress?.Report(new FileTransferProgress(totalBytesTransferred, totalBytes));
        }
    }

    private static void AddRemoteMetadata(
        IDictionary<string, RemoteFileMetadata> metadataByPath,
        string key,
        RemoteFileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            metadataByPath[key] = metadata;
        }
    }

    private static string BuildPythonMetadataCommand(string path) =>
        "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required for metadata lookup.' >&2; exit 127; }\n" +
        $"LMS_DIR={QuoteShellArgument(path)} python3 - <<'PY'\n" +
        "import grp\n" +
        "import json\n" +
        "import os\n" +
        "import pwd\n" +
        "import stat\n" +
        "import sys\n" +
        "\n" +
        "directory = os.environ['LMS_DIR']\n" +
        "items = []\n" +
        "try:\n" +
        "    with os.scandir(directory) as entries:\n" +
        "        for entry in entries:\n" +
        "            try:\n" +
        "                item_stat = os.lstat(entry.path)\n" +
        "                try:\n" +
        "                    owner = pwd.getpwuid(item_stat.st_uid).pw_name\n" +
        "                except KeyError:\n" +
        "                    owner = str(item_stat.st_uid)\n" +
        "                try:\n" +
        "                    group = grp.getgrgid(item_stat.st_gid).gr_name\n" +
        "                except KeyError:\n" +
        "                    group = str(item_stat.st_gid)\n" +
        "                link_target = os.readlink(entry.path) if stat.S_ISLNK(item_stat.st_mode) else ''\n" +
        "                items.append({\n" +
        "                    'Name': entry.name,\n" +
        "                    'Path': entry.path,\n" +
        "                    'OwnerName': owner,\n" +
        "                    'GroupName': group,\n" +
        "                    'SymbolicPermissions': stat.filemode(item_stat.st_mode),\n" +
        "                    'OctalPermissions': format(stat.S_IMODE(item_stat.st_mode), '03o'),\n" +
        "                    'LinkTarget': link_target,\n" +
        "                })\n" +
        "            except OSError:\n" +
        "                continue\n" +
        "except Exception as error:\n" +
        "    print(str(error), file=sys.stderr)\n" +
        "    raise SystemExit(1)\n" +
        "print(json.dumps(items, ensure_ascii=False))\n" +
        "PY";

    private static string BuildPythonFileSearchCommand(FileSearchRequest request) =>
        "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required for file search.' >&2; exit 127; }\n" +
        $"LMS_SEARCH_ROOT={QuoteShellArgument(request.RootPath)} " +
        $"LMS_SEARCH_NAME_PATTERN={QuoteShellArgument(request.NamePattern ?? string.Empty)} " +
        $"LMS_SEARCH_CONTAINS={QuoteShellArgument(request.ContainsText ?? string.Empty)} " +
        $"LMS_SEARCH_INCLUDE_FILES={(request.IncludeFiles ? "1" : "0")} " +
        $"LMS_SEARCH_INCLUDE_FOLDERS={(request.IncludeFolders ? "1" : "0")} " +
        $"LMS_SEARCH_CASE_INSENSITIVE={(request.CaseInsensitive ? "1" : "0")} " +
        $"LMS_SEARCH_MIN_BYTES={QuoteShellArgument(request.MinimumSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_MAX_BYTES={QuoteShellArgument(request.MaximumSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_MODIFIED_FROM={QuoteShellArgument(request.ModifiedFromUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_MODIFIED_TO={QuoteShellArgument(request.ModifiedToUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_CREATED_FROM={QuoteShellArgument(request.CreatedFromUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_CREATED_TO={QuoteShellArgument(request.CreatedToUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_ACCESSED_FROM={QuoteShellArgument(request.AccessedFromUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_ACCESSED_TO={QuoteShellArgument(request.AccessedToUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty)} " +
        $"LMS_SEARCH_MAX_RESULTS={QuoteShellArgument(request.MaxResults.ToString(CultureInfo.InvariantCulture))} " +
        "python3 - <<'PY'\n" +
        "import datetime\n" +
        "import fnmatch\n" +
        "import json\n" +
        "import os\n" +
        "import stat\n" +
        "import sys\n" +
        "import time\n" +
        "\n" +
        "root_path = os.environ['LMS_SEARCH_ROOT']\n" +
        "name_pattern = os.environ.get('LMS_SEARCH_NAME_PATTERN', '').strip()\n" +
        "contains_text = os.environ.get('LMS_SEARCH_CONTAINS', '')\n" +
        "include_files = os.environ.get('LMS_SEARCH_INCLUDE_FILES', '1') == '1'\n" +
        "include_folders = os.environ.get('LMS_SEARCH_INCLUDE_FOLDERS', '1') == '1'\n" +
        "case_insensitive = os.environ.get('LMS_SEARCH_CASE_INSENSITIVE', '1') == '1'\n" +
        "max_results = int(os.environ.get('LMS_SEARCH_MAX_RESULTS') or '500')\n" +
        "use_wildcards = '*' in name_pattern or '?' in name_pattern\n" +
        "\n" +
        "def parse_optional_number(name):\n" +
        "    value = os.environ.get(name, '').strip()\n" +
        "    return None if value == '' else float(value)\n" +
        "\n" +
        "min_bytes = parse_optional_number('LMS_SEARCH_MIN_BYTES')\n" +
        "max_bytes = parse_optional_number('LMS_SEARCH_MAX_BYTES')\n" +
        "modified_from = parse_optional_number('LMS_SEARCH_MODIFIED_FROM')\n" +
        "modified_to = parse_optional_number('LMS_SEARCH_MODIFIED_TO')\n" +
        "created_from = parse_optional_number('LMS_SEARCH_CREATED_FROM')\n" +
        "created_to = parse_optional_number('LMS_SEARCH_CREATED_TO')\n" +
        "accessed_from = parse_optional_number('LMS_SEARCH_ACCESSED_FROM')\n" +
        "accessed_to = parse_optional_number('LMS_SEARCH_ACCESSED_TO')\n" +
        "if not os.path.isdir(root_path):\n" +
        "    raise SystemExit(f'{root_path} was not found on the remote host.')\n" +
        "\n" +
        "def normalize(value):\n" +
        "    return value.lower() if case_insensitive else value\n" +
        "\n" +
        "def name_matches(candidate):\n" +
        "    if not name_pattern:\n" +
        "        return True\n" +
        "    haystack = normalize(candidate)\n" +
        "    needle = normalize(name_pattern)\n" +
        "    if use_wildcards:\n" +
        "        return fnmatch.fnmatch(haystack, needle)\n" +
        "    return needle in haystack\n" +
        "\n" +
        "def file_contains_text(path):\n" +
        "    if not contains_text:\n" +
        "        return True\n" +
        "    needle = normalize(contains_text)\n" +
        "    carry = ''\n" +
        "    try:\n" +
        "        with open(path, 'r', encoding='utf-8', errors='ignore') as handle:\n" +
        "            while True:\n" +
        "                chunk = handle.read(4096)\n" +
        "                if not chunk:\n" +
        "                    return needle in normalize(carry)\n" +
        "                if '\\x00' in chunk:\n" +
        "                    return False\n" +
        "                haystack = carry + chunk\n" +
        "                if needle in normalize(haystack):\n" +
        "                    return True\n" +
        "                carry_length = max(len(contains_text) - 1, 0)\n" +
        "                carry = haystack[-carry_length:] if carry_length else ''\n" +
        "    except (OSError, UnicodeError):\n" +
        "        return False\n" +
        "\n" +
        "results = []\n" +
        "limit_reached = False\n" +
        "stack = [root_path]\n" +
        "last_progress = 0.0\n" +
        "\n" +
        "def report_progress(path):\n" +
        "    global last_progress\n" +
        "    now = time.monotonic()\n" +
        "    if now - last_progress < 0.3:\n" +
        "        return\n" +
        "    sys.stderr.write('LMS_PROGRESS\\t' + path + '\\n')\n" +
        "    sys.stderr.flush()\n" +
        "    last_progress = now\n" +
        "\n" +
        "while stack and len(results) < max_results:\n" +
        "    current = stack.pop()\n" +
        "    report_progress(current)\n" +
        "    try:\n" +
        "        with os.scandir(current) as entries:\n" +
        "            for entry in entries:\n" +
        "                try:\n" +
        "                    entry_stat = os.lstat(entry.path)\n" +
        "                except OSError:\n" +
        "                    continue\n" +
        "\n" +
        "                is_directory = stat.S_ISDIR(entry_stat.st_mode)\n" +
        "                is_link = stat.S_ISLNK(entry_stat.st_mode)\n" +
        "                is_regular_file = stat.S_ISREG(entry_stat.st_mode)\n" +
        "\n" +
        "                if is_directory and not is_link:\n" +
        "                    stack.append(entry.path)\n" +
        "\n" +
        "                if not name_matches(entry.name):\n" +
        "                    continue\n" +
        "                if modified_from is not None and entry_stat.st_mtime < modified_from:\n" +
        "                    continue\n" +
        "                if modified_to is not None and entry_stat.st_mtime > modified_to:\n" +
        "                    continue\n" +
        "                created_at = getattr(entry_stat, 'st_birthtime', None)\n" +
        "                if created_at is None:\n" +
        "                    created_at = getattr(entry_stat, 'st_ctime', None)\n" +
        "                if created_from is not None and created_at is not None and created_at < created_from:\n" +
        "                    continue\n" +
        "                if created_to is not None and created_at is not None and created_at > created_to:\n" +
        "                    continue\n" +
        "                if accessed_from is not None and entry_stat.st_atime < accessed_from:\n" +
        "                    continue\n" +
        "                if accessed_to is not None and entry_stat.st_atime > accessed_to:\n" +
        "                    continue\n" +
        "\n" +
        "                if is_directory:\n" +
        "                    if not include_folders:\n" +
        "                        continue\n" +
        "                    if min_bytes is not None or max_bytes is not None or contains_text:\n" +
        "                        continue\n" +
        "                    item_type = 1\n" +
        "                    size_bytes = 0\n" +
        "                    matched_contents = False\n" +
        "                else:\n" +
        "                    if not include_files:\n" +
        "                        continue\n" +
        "                    if min_bytes is not None and entry_stat.st_size < min_bytes:\n" +
        "                        continue\n" +
        "                    if max_bytes is not None and entry_stat.st_size > max_bytes:\n" +
        "                        continue\n" +
        "                    if contains_text:\n" +
        "                        if not is_regular_file or not file_contains_text(entry.path):\n" +
        "                            continue\n" +
        "                    item_type = 2 if is_link else 0\n" +
        "                    size_bytes = entry_stat.st_size\n" +
        "                    matched_contents = bool(contains_text)\n" +
        "\n" +
        "                last_modified = datetime.datetime.fromtimestamp(entry_stat.st_mtime, datetime.timezone.utc).isoformat().replace('+00:00', 'Z')\n" +
        "                parent_path = os.path.dirname(entry.path) or '/'\n" +
        "                link_target = ''\n" +
        "                if is_link:\n" +
        "                    try:\n" +
        "                        link_target = os.readlink(entry.path)\n" +
        "                    except OSError:\n" +
        "                        link_target = ''\n" +
        "\n" +
        "                results.append({\n" +
        "                    'Name': entry.name,\n" +
        "                    'FullPath': entry.path,\n" +
        "                    'ParentPath': parent_path,\n" +
        "                    'ItemType': item_type,\n" +
        "                    'SizeBytes': size_bytes,\n" +
        "                    'LastModifiedUtc': last_modified,\n" +
        "                    'MatchedContents': matched_contents,\n" +
        "                    'LinkTarget': link_target,\n" +
        "                })\n" +
        "                sys.stderr.write('LMS_MATCH\\t' + json.dumps(results[-1], ensure_ascii=False) + '\\n')\n" +
        "                sys.stderr.flush()\n" +
        "\n" +
        "                if len(results) >= max_results:\n" +
        "                    limit_reached = True\n" +
        "                    break\n" +
        "    except OSError:\n" +
        "        continue\n" +
        "\n" +
        "print(json.dumps({'Results': results, 'LimitReached': limit_reached}, ensure_ascii=False))\n" +
        "PY";

    private static async Task<string> ReadRemoteSearchErrorStreamAsync(
        SshCommand command,
        IProgress<FileSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(command.ExtendedOutputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var errorBuilder = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("LMS_PROGRESS\t", StringComparison.Ordinal))
            {
                var currentPath = line["LMS_PROGRESS\t".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(currentPath))
                {
                    progress?.Report(new FileSearchProgress(currentPath, DateTimeOffset.UtcNow));
                }

                continue;
            }

            if (line.StartsWith("LMS_MATCH\t", StringComparison.Ordinal))
            {
                var payload = line["LMS_MATCH\t".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    try
                    {
                        var result = JsonSerializer.Deserialize<RemoteFileSearchResult>(payload);
                        if (result is not null)
                        {
                            progress?.Report(new FileSearchProgress(
                                result.ParentPath,
                                DateTimeOffset.UtcNow,
                                new FileSearchMatch(
                                    result.Name,
                                    result.FullPath,
                                    result.ParentPath,
                                    result.ItemType,
                                    result.SizeBytes,
                                    result.LastModifiedUtc,
                                    result.MatchedContents,
                                    result.LinkTarget)));
                            continue;
                        }
                    }
                    catch (JsonException)
                    {
                        // Fall through and preserve the raw line as stderr output.
                    }
                }
            }

            if (errorBuilder.Length > 0)
            {
                errorBuilder.AppendLine();
            }

            errorBuilder.Append(line);
        }

        return errorBuilder.ToString();
    }

    private static string BuildRemoteGzipArchiveCommand(string sourcePath, string destinationArchivePath) =>
        $"SOURCE_PATH={QuoteShellArgument(sourcePath)} DESTINATION_ARCHIVE_PATH={QuoteShellArgument(destinationArchivePath)} sh - <<'SH'\n" +
        "set -eu\n" +
        "if [ ! -e \"$SOURCE_PATH\" ]; then echo \"$SOURCE_PATH was not found on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -e \"$DESTINATION_ARCHIVE_PATH\" ]; then echo \"$DESTINATION_ARCHIVE_PATH already exists on the remote host.\" >&2; exit 1; fi\n" +
        "destination_parent=$(dirname -- \"$DESTINATION_ARCHIVE_PATH\")\n" +
        "if [ ! -d \"$destination_parent\" ]; then echo \"$destination_parent does not exist on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -d \"$SOURCE_PATH\" ]; then\n" +
        "  source_parent=$(dirname -- \"$SOURCE_PATH\")\n" +
        "  source_name=$(basename -- \"$SOURCE_PATH\")\n" +
        "  tar -czf \"$DESTINATION_ARCHIVE_PATH\" -C \"$source_parent\" \"$source_name\"\n" +
        "else\n" +
        "  gzip -c -- \"$SOURCE_PATH\" > \"$DESTINATION_ARCHIVE_PATH\"\n" +
        "fi\n" +
        "SH";

    private static string BuildRemoteTarGzipArchiveCommand(string sourcePath, string destinationArchivePath) =>
        $"SOURCE_PATH={QuoteShellArgument(sourcePath)} DESTINATION_ARCHIVE_PATH={QuoteShellArgument(destinationArchivePath)} sh - <<'SH'\n" +
        "set -eu\n" +
        "if [ ! -e \"$SOURCE_PATH\" ]; then echo \"$SOURCE_PATH was not found on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -e \"$DESTINATION_ARCHIVE_PATH\" ]; then echo \"$DESTINATION_ARCHIVE_PATH already exists on the remote host.\" >&2; exit 1; fi\n" +
        "destination_parent=$(dirname -- \"$DESTINATION_ARCHIVE_PATH\")\n" +
        "if [ ! -d \"$destination_parent\" ]; then echo \"$destination_parent does not exist on the remote host.\" >&2; exit 1; fi\n" +
        "source_parent=$(dirname -- \"$SOURCE_PATH\")\n" +
        "source_name=$(basename -- \"$SOURCE_PATH\")\n" +
        "tar -czf \"$DESTINATION_ARCHIVE_PATH\" -C \"$source_parent\" \"$source_name\"\n" +
        "SH";

    private static string BuildRemoteSevenZipCreateCommand(string sourcePath, string destinationArchivePath) =>
        $"SOURCE_PATH={QuoteShellArgument(sourcePath)} DESTINATION_ARCHIVE_PATH={QuoteShellArgument(destinationArchivePath)} sh - <<'SH'\n" +
        "set -eu\n" +
        "if command -v 7z >/dev/null 2>&1; then LMS_7Z=7z; elif command -v 7zz >/dev/null 2>&1; then LMS_7Z=7zz; else echo '7z or 7zz is required for 7Zip support.' >&2; exit 127; fi\n" +
        "if [ ! -e \"$SOURCE_PATH\" ]; then echo \"$SOURCE_PATH was not found on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -e \"$DESTINATION_ARCHIVE_PATH\" ]; then echo \"$DESTINATION_ARCHIVE_PATH already exists on the remote host.\" >&2; exit 1; fi\n" +
        "destination_parent=$(dirname -- \"$DESTINATION_ARCHIVE_PATH\")\n" +
        "if [ ! -d \"$destination_parent\" ]; then echo \"$destination_parent does not exist on the remote host.\" >&2; exit 1; fi\n" +
        "source_parent=$(dirname -- \"$SOURCE_PATH\")\n" +
        "source_name=$(basename -- \"$SOURCE_PATH\")\n" +
        "cd -- \"$source_parent\"\n" +
        "\"$LMS_7Z\" a -t7z \"$DESTINATION_ARCHIVE_PATH\" \"$source_name\"\n" +
        "SH";

    private static string BuildRemoteRarCreateCommand(string sourcePath, string destinationArchivePath) =>
        $"SOURCE_PATH={QuoteShellArgument(sourcePath)} DESTINATION_ARCHIVE_PATH={QuoteShellArgument(destinationArchivePath)} sh - <<'SH'\n" +
        "set -eu\n" +
        "command -v rar >/dev/null 2>&1 || { echo 'rar is required for RAR compression.' >&2; exit 127; }\n" +
        "if [ ! -e \"$SOURCE_PATH\" ]; then echo \"$SOURCE_PATH was not found on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -e \"$DESTINATION_ARCHIVE_PATH\" ]; then echo \"$DESTINATION_ARCHIVE_PATH already exists on the remote host.\" >&2; exit 1; fi\n" +
        "destination_parent=$(dirname -- \"$DESTINATION_ARCHIVE_PATH\")\n" +
        "if [ ! -d \"$destination_parent\" ]; then echo \"$destination_parent does not exist on the remote host.\" >&2; exit 1; fi\n" +
        "source_parent=$(dirname -- \"$SOURCE_PATH\")\n" +
        "source_name=$(basename -- \"$SOURCE_PATH\")\n" +
        "cd -- \"$source_parent\"\n" +
        "rar a -r \"$DESTINATION_ARCHIVE_PATH\" \"$source_name\"\n" +
        "SH";

    private static string BuildRemoteGzipExtractCommand(string archivePath, string destinationDirectoryPath) =>
        $"ARCHIVE_PATH={QuoteShellArgument(archivePath)} DESTINATION_DIRECTORY_PATH={QuoteShellArgument(destinationDirectoryPath)} sh - <<'SH'\n" +
        "set -eu\n" +
        "if [ ! -f \"$ARCHIVE_PATH\" ]; then echo \"$ARCHIVE_PATH was not found on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -e \"$DESTINATION_DIRECTORY_PATH\" ] && [ \"$(find \"$DESTINATION_DIRECTORY_PATH\" -mindepth 1 -maxdepth 1 2>/dev/null | head -n 1)\" ]; then echo \"$DESTINATION_DIRECTORY_PATH already exists and is not empty on the remote host.\" >&2; exit 1; fi\n" +
        "destination_parent=$(dirname -- \"$DESTINATION_DIRECTORY_PATH\")\n" +
        "if [ ! -d \"$destination_parent\" ]; then echo \"$destination_parent does not exist on the remote host.\" >&2; exit 1; fi\n" +
        "mkdir -p -- \"$DESTINATION_DIRECTORY_PATH\"\n" +
        "archive_name=$(basename -- \"$ARCHIVE_PATH\")\n" +
        "output_name=${archive_name%.gz}\n" +
        "if [ \"$output_name\" = \"$archive_name\" ] || [ -z \"$output_name\" ]; then output_name=content; fi\n" +
        "gzip -dc -- \"$ARCHIVE_PATH\" > \"$DESTINATION_DIRECTORY_PATH/$output_name\"\n" +
        "SH";

    private static string BuildRemoteTarGzipExtractCommand(string archivePath, string destinationDirectoryPath) =>
        $"ARCHIVE_PATH={QuoteShellArgument(archivePath)} DESTINATION_DIRECTORY_PATH={QuoteShellArgument(destinationDirectoryPath)} sh - <<'SH'\n" +
        "set -eu\n" +
        "if [ ! -f \"$ARCHIVE_PATH\" ]; then echo \"$ARCHIVE_PATH was not found on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -e \"$DESTINATION_DIRECTORY_PATH\" ] && [ \"$(find \"$DESTINATION_DIRECTORY_PATH\" -mindepth 1 -maxdepth 1 2>/dev/null | head -n 1)\" ]; then echo \"$DESTINATION_DIRECTORY_PATH already exists and is not empty on the remote host.\" >&2; exit 1; fi\n" +
        "destination_parent=$(dirname -- \"$DESTINATION_DIRECTORY_PATH\")\n" +
        "if [ ! -d \"$destination_parent\" ]; then echo \"$destination_parent does not exist on the remote host.\" >&2; exit 1; fi\n" +
        "mkdir -p -- \"$DESTINATION_DIRECTORY_PATH\"\n" +
        "tar -xzf \"$ARCHIVE_PATH\" -C \"$DESTINATION_DIRECTORY_PATH\"\n" +
        "SH";

    private static string BuildRemoteSevenZipExtractCommand(
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format) =>
        $"ARCHIVE_PATH={QuoteShellArgument(archivePath)} DESTINATION_DIRECTORY_PATH={QuoteShellArgument(destinationDirectoryPath)} sh - <<'SH'\n" +
        "set -eu\n" +
        $"if command -v 7z >/dev/null 2>&1; then LMS_7Z=7z; elif command -v 7zz >/dev/null 2>&1; then LMS_7Z=7zz; else echo '7z or 7zz is required for {ArchiveFormatSupport.GetDisplayName(format)} extraction.' >&2; exit 127; fi\n" +
        "if [ ! -f \"$ARCHIVE_PATH\" ]; then echo \"$ARCHIVE_PATH was not found on the remote host.\" >&2; exit 1; fi\n" +
        "if [ -e \"$DESTINATION_DIRECTORY_PATH\" ] && [ \"$(find \"$DESTINATION_DIRECTORY_PATH\" -mindepth 1 -maxdepth 1 2>/dev/null | head -n 1)\" ]; then echo \"$DESTINATION_DIRECTORY_PATH already exists and is not empty on the remote host.\" >&2; exit 1; fi\n" +
        "destination_parent=$(dirname -- \"$DESTINATION_DIRECTORY_PATH\")\n" +
        "if [ ! -d \"$destination_parent\" ]; then echo \"$destination_parent does not exist on the remote host.\" >&2; exit 1; fi\n" +
        "mkdir -p -- \"$DESTINATION_DIRECTORY_PATH\"\n" +
        "\"$LMS_7Z\" x -y -o\"$DESTINATION_DIRECTORY_PATH\" \"$ARCHIVE_PATH\"\n" +
        "SH";

    private static string BuildRemoteTarGzipListCommand(string archivePath) =>
        $"tar -tzf {QuoteShellArgument(archivePath)}";

    private static string BuildRemoteSevenZipListCommand(string archivePath) =>
        "if command -v 7z >/dev/null 2>&1; then LMS_7Z=7z; " +
        "elif command -v 7zz >/dev/null 2>&1; then LMS_7Z=7zz; " +
        "else echo '7z or 7zz is required to preview this archive.' >&2; exit 127; fi\n" +
        $"\"$LMS_7Z\" l -slt {QuoteShellArgument(archivePath)}";

    private static string BuildPythonArchiveCommand(string sourcePath, string destinationZipPath) =>
        "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required for zip support.' >&2; exit 127; }\n" +
        $"SOURCE_PATH={QuoteShellArgument(sourcePath)} DESTINATION_ZIP_PATH={QuoteShellArgument(destinationZipPath)} python3 - <<'PY'\n" +
        "from pathlib import Path\n" +
        "import os\n" +
        "import zipfile\n" +
        "\n" +
        "source = Path(os.environ['SOURCE_PATH'])\n" +
        "destination = Path(os.environ['DESTINATION_ZIP_PATH'])\n" +
        "if not source.exists():\n" +
        "    raise SystemExit(f'{source} was not found on the remote host.')\n" +
        "if destination.exists():\n" +
        "    raise SystemExit(f'{destination} already exists on the remote host.')\n" +
        "if not destination.parent.exists():\n" +
        "    raise SystemExit(f'{destination.parent} does not exist on the remote host.')\n" +
        "with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED) as archive:\n" +
        "    if source.is_dir():\n" +
        "        base = source.parent\n" +
        "        wrote = False\n" +
        "        for path in sorted(source.rglob('*')):\n" +
        "            relative = path.relative_to(base).as_posix()\n" +
        "            if path.is_dir():\n" +
        "                archive.writestr(relative.rstrip('/') + '/', '')\n" +
        "            else:\n" +
        "                archive.write(path, relative)\n" +
        "            wrote = True\n" +
        "        if not wrote:\n" +
        "            archive.writestr((source.name or 'archive').rstrip('/') + '/', '')\n" +
        "    else:\n" +
        "        archive.write(source, source.name)\n" +
        "PY";

    private static string BuildPythonExtractCommand(string archivePath, string destinationDirectoryPath) =>
        "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required for unzip support.' >&2; exit 127; }\n" +
        $"ARCHIVE_PATH={QuoteShellArgument(archivePath)} DESTINATION_DIRECTORY_PATH={QuoteShellArgument(destinationDirectoryPath)} python3 - <<'PY'\n" +
        "from pathlib import Path\n" +
        "import os\n" +
        "import zipfile\n" +
        "\n" +
        "archive_path = Path(os.environ['ARCHIVE_PATH'])\n" +
        "destination = Path(os.environ['DESTINATION_DIRECTORY_PATH'])\n" +
        "if not archive_path.is_file():\n" +
        "    raise SystemExit(f'{archive_path} was not found on the remote host.')\n" +
        "if destination.exists() and any(destination.iterdir()):\n" +
        "    raise SystemExit(f'{destination} already exists and is not empty on the remote host.')\n" +
        "if not destination.exists() and not destination.parent.exists():\n" +
        "    raise SystemExit(f'{destination.parent} does not exist on the remote host.')\n" +
        "destination.mkdir(parents=False, exist_ok=True)\n" +
        "with zipfile.ZipFile(archive_path, 'r') as archive:\n" +
        "    archive.extractall(destination)\n" +
        "PY";

    private static string BuildPythonListZipCommand(string archivePath, int maxEntries) =>
        "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required for zip preview support.' >&2; exit 127; }\n" +
        $"ARCHIVE_PATH={QuoteShellArgument(archivePath)} LMS_MAX_ENTRIES={QuoteShellArgument(maxEntries.ToString(CultureInfo.InvariantCulture))} python3 - <<'PY'\n" +
        "from datetime import datetime, timezone\n" +
        "from pathlib import PurePosixPath, Path\n" +
        "import json\n" +
        "import os\n" +
        "import zipfile\n" +
        "\n" +
        "archive_path = Path(os.environ['ARCHIVE_PATH'])\n" +
        "max_entries = int(os.environ['LMS_MAX_ENTRIES'])\n" +
        "if not archive_path.is_file():\n" +
        "    raise SystemExit(f'{archive_path} was not found on the remote host.')\n" +
        "entries = []\n" +
        "with zipfile.ZipFile(archive_path, 'r') as archive:\n" +
        "    for info in archive.infolist()[:max_entries]:\n" +
        "        full_name = info.filename.replace('\\\\', '/')\n" +
        "        trimmed = full_name.rstrip('/')\n" +
        "        name = PurePosixPath(trimmed).name if trimmed else full_name\n" +
        "        modified = None\n" +
        "        try:\n" +
        "            modified = datetime(*info.date_time, tzinfo=timezone.utc).isoformat()\n" +
        "        except Exception:\n" +
        "            modified = None\n" +
        "        entries.append({\n" +
        "            'Name': name or trimmed,\n" +
        "            'FullName': full_name,\n" +
        "            'ItemType': 'Folder' if info.is_dir() else 'File',\n" +
        "            'SizeBytes': 0 if info.is_dir() else info.file_size,\n" +
        "            'LastModifiedUtc': modified,\n" +
        "        })\n" +
        "print(json.dumps(entries))\n" +
        "PY";

    private static IReadOnlyList<ArchiveEntry> DeserializeRemoteZipEntries(string output)
    {
        var entries = string.IsNullOrWhiteSpace(output)
            ? []
            : JsonSerializer.Deserialize<RemoteArchiveEntry[]>(output) ?? [];

        return entries
            .Select(entry => new ArchiveEntry(
                entry.Name,
                entry.FullName,
                ResolveArchiveEntryItemType(entry.ItemType),
                entry.SizeBytes,
                entry.LastModifiedUtc))
            .ToArray();
    }

    private static IReadOnlyList<ArchiveEntry> ListRemoteGzipEntries(string archivePath)
    {
        var fileName = GetFileName(archivePath);
        if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^3];
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "content";
        }

        return
        [
            new ArchiveEntry(fileName, fileName, SftpItemType.File, 0, null)
        ];
    }

    private static IReadOnlyList<ArchiveEntry> ParseSimpleArchiveEntryList(string output, int maxEntries) =>
        output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(maxEntries)
            .Select(line =>
            {
                var fullName = line.Replace('\\', '/');
                var isDirectory = fullName.EndsWith("/", StringComparison.Ordinal);
                var trimmedName = fullName.TrimEnd('/');
                var name = GetArchiveEntryName(trimmedName);
                return new ArchiveEntry(
                    string.IsNullOrWhiteSpace(name) ? trimmedName : name,
                    fullName,
                    isDirectory ? SftpItemType.Folder : SftpItemType.File,
                    0,
                    null);
            })
            .ToArray();

    private static IReadOnlyList<ArchiveEntry> ParseSevenZipTechnicalList(string output, int maxEntries)
    {
        var entries = new List<ArchiveEntry>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                AddSevenZipEntry(block, entries, maxEntries);
                block.Clear();
                if (entries.Count >= maxEntries)
                {
                    break;
                }

                continue;
            }

            var separatorIndex = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            block[line[..separatorIndex]] = line[(separatorIndex + 3)..];
        }

        AddSevenZipEntry(block, entries, maxEntries);
        return entries;
    }

    private static void AddSevenZipEntry(
        IReadOnlyDictionary<string, string> block,
        ICollection<ArchiveEntry> entries,
        int maxEntries)
    {
        if (entries.Count >= maxEntries ||
            !block.TryGetValue("Path", out var path) ||
            string.IsNullOrWhiteSpace(path) ||
            (!block.ContainsKey("Size") && !block.ContainsKey("Folder") && !block.ContainsKey("Attributes")))
        {
            return;
        }

        var isDirectory =
            (block.TryGetValue("Folder", out var folder) && folder.Equals("+", StringComparison.OrdinalIgnoreCase)) ||
            (block.TryGetValue("Attributes", out var attributes) && attributes.Contains("D", StringComparison.OrdinalIgnoreCase)) ||
            path.EndsWith("/", StringComparison.Ordinal);
        var sizeBytes = block.TryGetValue("Size", out var sizeText) &&
                        long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize)
            ? parsedSize
            : 0;
        var modified = block.TryGetValue("Modified", out var modifiedText) &&
                       DateTimeOffset.TryParse(modifiedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedModified)
            ? (DateTimeOffset?)parsedModified
            : null;
        var fullName = path.Replace('\\', '/');
        var name = GetArchiveEntryName(fullName.TrimEnd('/'));

        entries.Add(new ArchiveEntry(
            string.IsNullOrWhiteSpace(name) ? fullName : name,
            fullName,
            isDirectory ? SftpItemType.Folder : SftpItemType.File,
            isDirectory ? 0 : sizeBytes,
            modified));
    }

    private static string GetArchiveEntryName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    private static string GetFileName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private static SftpItemType ResolveArchiveEntryItemType(string itemType) =>
        itemType.Equals("Folder", StringComparison.OrdinalIgnoreCase)
            ? SftpItemType.Folder
            : SftpItemType.File;

    private static SftpItem MapItem(ISftpFile item, IReadOnlyDictionary<string, RemoteFileMetadata> metadataByPath)
    {
        var itemType = item.IsSymbolicLink
            ? SftpItemType.Link
            : item.IsDirectory
                ? SftpItemType.Folder
                : item.IsRegularFile
                    ? SftpItemType.File
                    : SftpItemType.Other;
        var metadata = ResolveRemoteMetadata(item, metadataByPath);

        return new SftpItem(
            item.Name,
            item.FullName,
            itemType,
            item.IsDirectory ? 0 : item.Length,
            item.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
            NullIfWhiteSpace(metadata?.SymbolicPermissions) ?? FormatPermissions(item.Attributes),
            NullIfWhiteSpace(metadata?.OwnerName) ?? item.UserId.ToString(CultureInfo.InvariantCulture),
            NullIfWhiteSpace(metadata?.GroupName) ?? item.GroupId.ToString(CultureInfo.InvariantCulture),
            NullIfWhiteSpace(metadata?.OctalPermissions) ?? FormatPermissionsOctal(item.Attributes),
            itemType == SftpItemType.Link ? NullIfWhiteSpace(metadata?.LinkTarget) ?? string.Empty : string.Empty);
    }

    private static RemoteFileMetadata? ResolveRemoteMetadata(ISftpFile item, IReadOnlyDictionary<string, RemoteFileMetadata> metadataByPath)
    {
        if (metadataByPath.TryGetValue(item.FullName, out var metadata))
        {
            return metadata;
        }

        return metadataByPath.TryGetValue(item.Name, out metadata) ? metadata : null;
    }

    private static string FormatPermissions(SftpFileAttributes attributes)
    {
        var chars = new char[10];
        chars[0] = attributes.IsDirectory
            ? 'd'
            : attributes.IsSymbolicLink
                ? 'l'
                : '-';

        chars[1] = attributes.OwnerCanRead ? 'r' : '-';
        chars[2] = attributes.OwnerCanWrite ? 'w' : '-';
        chars[3] = attributes.OwnerCanExecute ? 'x' : '-';
        chars[4] = attributes.GroupCanRead ? 'r' : '-';
        chars[5] = attributes.GroupCanWrite ? 'w' : '-';
        chars[6] = attributes.GroupCanExecute ? 'x' : '-';
        chars[7] = attributes.OthersCanRead ? 'r' : '-';
        chars[8] = attributes.OthersCanWrite ? 'w' : '-';
        chars[9] = attributes.OthersCanExecute ? 'x' : '-';

        return new string(chars);
    }

    private static string FormatPermissionsOctal(SftpFileAttributes attributes)
    {
        var special = 0;
        if (attributes.IsUIDBitSet)
        {
            special += 4;
        }

        if (attributes.IsGroupIDBitSet)
        {
            special += 2;
        }

        if (attributes.IsStickyBitSet)
        {
            special += 1;
        }

        var owner = (attributes.OwnerCanRead ? 4 : 0) +
                    (attributes.OwnerCanWrite ? 2 : 0) +
                    (attributes.OwnerCanExecute ? 1 : 0);
        var group = (attributes.GroupCanRead ? 4 : 0) +
                    (attributes.GroupCanWrite ? 2 : 0) +
                    (attributes.GroupCanExecute ? 1 : 0);
        var other = (attributes.OthersCanRead ? 4 : 0) +
                    (attributes.OthersCanWrite ? 2 : 0) +
                    (attributes.OthersCanExecute ? 1 : 0);

        return special > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{special}{owner}{group}{other}")
            : string.Create(CultureInfo.InvariantCulture, $"{owner}{group}{other}");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record RemoteFileMetadata(
        string Name,
        string Path,
        string OwnerName,
        string GroupName,
        string SymbolicPermissions,
        string OctalPermissions,
        string LinkTarget);

    private sealed record RemoteArchiveEntry(
        string Name,
        string FullName,
        string ItemType,
        long SizeBytes,
        DateTimeOffset? LastModifiedUtc);

    private sealed record RemoteFileSearchPayload(
        IReadOnlyList<RemoteFileSearchResult> Results,
        bool LimitReached);

    private sealed record RemoteFileSearchResult(
        string Name,
        string FullPath,
        string ParentPath,
        SftpItemType ItemType,
        long SizeBytes,
        DateTimeOffset? LastModifiedUtc,
        bool MatchedContents,
        string LinkTarget);
}
