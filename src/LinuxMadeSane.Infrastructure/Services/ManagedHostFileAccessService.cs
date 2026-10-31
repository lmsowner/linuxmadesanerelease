// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

// Guardrail: UI code must go through this service for host file access. Local-vs-SSH
// routing and transient credential resolution belong here, not in pages or components.
public sealed class ManagedHostFileAccessService(
    ILocalFileBrowsingService localFileBrowsingService,
    ISftpFileBrowsingService sftpFileBrowsingService,
    ManagedHostSshCredentialResolver sshCredentialResolver,
    ITransientConnectionSecretStore transientConnectionSecretStore,
    PrivilegedFileBrowsingService? privilegedFileBrowsingService = null) : IManagedHostFileAccessService
{
    public Task<FolderDuplicateResult> ScanFolderDuplicatesAsync(ManagedHost host, string path,
        ManagedHostConnectionProfile profile, CancellationToken cancellationToken = default) =>
        GetPrivilegedService().ProcessFolderDuplicatesAsync(host, path, profile, null, cancellationToken);

    public Task<FolderDuplicateResult> MoveFolderDuplicatesAsync(ManagedHost host, FolderDuplicateResult scan,
        ManagedHostConnectionProfile profile, CancellationToken cancellationToken = default) =>
        GetPrivilegedService().ProcessFolderDuplicatesAsync(host, scan.RootPath, profile, scan, cancellationToken);

    private static readonly SemaphoreSlim RemotePdfSearchGate = new(1);
    public async Task<ManagedHostConnectionValidationResult> ValidateAccessAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (ShouldUseLocalFileBrowsing(host, connectionProfile) && !connectionProfile.UseSudo)
        {
            return new ManagedHostConnectionValidationResult(true, string.Empty);
        }

        if (!ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            var username = connectionProfile.Username.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                return new ManagedHostConnectionValidationResult(false, "Provide a username to browse files.");
            }

            var secrets = transientConnectionSecretStore.Get(connectionProfile.SecretHandle);
            if (string.IsNullOrWhiteSpace(secrets.Password) && string.IsNullOrWhiteSpace(secrets.PrivateKey))
            {
                if (!connectionProfile.PreferStoredCredentials)
                {
                    return new ManagedHostConnectionValidationResult(false, "Provide a password or private key to browse files.");
                }

                var resolution = await sshCredentialResolver.TryResolveAsync(host, cancellationToken);
                if (!resolution.Success)
                {
                    return new ManagedHostConnectionValidationResult(false, resolution.FailureMessage);
                }
            }
        }

        return connectionProfile.UseSudo
            ? await GetPrivilegedService().ValidateAccessAsync(host, connectionProfile, cancellationToken)
            : new ManagedHostConnectionValidationResult(true, string.Empty);
    }

    public Task<string> GetHomeDirectoryAsync(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Task.FromResult(string.IsNullOrWhiteSpace(home) ? host.DefaultWorkingDirectory : home);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.GetHomeDirectoryAsync(
            host,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().ListItemsAsync(host, path, connectionProfile, cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.ListItemsAsync(host.DefaultWorkingDirectory, path, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ListItemsAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public async Task<FileSearchResponse> SearchAsync(
        ManagedHost host,
        FileSearchRequest request,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SearchCoreAsync(host, request, connectionProfile, progress, cancellationToken);
        if (!response.PdfSearchSupportRequired || !request.RequiresContentScan) return response;

        var remote = !ShouldUseLocalFileBrowsing(host, connectionProfile);
        progress?.Report(new(request.RootPath, DateTimeOffset.UtcNow,
            StatusMessage: $"Preparing PDF search for {host.Name}… Your search will continue automatically."));
        bool prepared;
        try
        {
            prepared = await GetPrivilegedService().EnsurePdfSearchSupportAsync(host, connectionProfile, cancellationToken);
        }
        catch (Exception exception) when (remote && exception is not OperationCanceledException)
        {
            // Read-only appliances and accounts without package-management rights still support PDF search.
            prepared = false;
        }
        if (!prepared && remote) return await SearchRemotePdfsOnLmsAsync(host, request, connectionProfile, response, progress, cancellationToken);
        if (!prepared) throw new InvalidOperationException("This LMS host does not support automatic installation of PDF search tools.");
        progress?.Report(new(request.RootPath, DateTimeOffset.UtcNow, StatusMessage: "PDF search is ready. Continuing your search…"));
        response = await SearchCoreAsync(host, request, connectionProfile, progress, cancellationToken);
        if (response.PdfSearchSupportRequired && remote)
            return await SearchRemotePdfsOnLmsAsync(host, request, connectionProfile, response, progress, cancellationToken);
        if (response.PdfSearchSupportRequired)
            throw new InvalidOperationException($"PDF search support was installed on {host.Name}, but is not available to the selected file-browser account. Reconnect and retry.");
        return response;
    }

    private async Task<FileSearchResponse> SearchRemotePdfsOnLmsAsync(ManagedHost host, FileSearchRequest request,
        ManagedHostConnectionProfile profile, FileSearchResponse response, IProgress<FileSearchProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(request.RootPath, DateTimeOffset.UtcNow,
            StatusMessage: $"Searching PDFs from {host.Name} using LMS…"));
        await RemotePdfSearchGate.WaitAsync(token);
        string? scratch = null;
        try
        {
            if (!await GetPrivilegedService().EnsurePdfSearchSupportAsync(AiLocalMachine.CreateManagedHost(), new(Environment.UserName, null, true), token))
                throw new InvalidOperationException("LMS could not prepare its PDF search tools.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            scratch = Path.Combine(Path.GetTempPath(), "lms-pdf-search-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var matches = response.Results.ToList();
            var failed = 0;
            var limitReached = response.LimitReached;
            foreach (var pdf in response.PendingPdfFiles)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (matches.Count >= request.MaxResults) { limitReached = true; break; }
                var localPath = Path.Combine(scratch, "document.pdf");
                using var documentTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                documentTimeout.CancelAfter(TimeSpan.FromMinutes(1));
                try
                {
                    progress?.Report(new(pdf.FullPath, DateTimeOffset.UtcNow,
                        StatusMessage: $"Searching {pdf.Name} from {host.Name}…"));
                    // One private, bounded spool file at a time; never load the PDF into managed memory.
                    await using (var source = await OpenReadStreamAsync(host, pdf.FullPath, profile, documentTimeout.Token))
                    await using (var destination = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        var buffer = new byte[65536];
                        long bytes = 0;
                        while (await source.ReadAsync(buffer, documentTimeout.Token) is var count && count > 0)
                        {
                            bytes += count;
                            if (bytes > 512L * 1024 * 1024) throw new IOException("PDF exceeds the search spool limit.");
                            await destination.WriteAsync(buffer.AsMemory(0, count), documentTimeout.Token);
                        }
                    }
                    var local = await localFileBrowsingService.SearchAsync(scratch,
                        new(scratch, IncludeFolders: false, ContainsText: request.ContainsText, CaseInsensitive: request.CaseInsensitive,
                            MaxResults: 1, ContentTypes: FileSearchContentTypes.Pdf, IncludeSubfolders: false, CandidateNames: ["document.pdf"]),
                        cancellationToken: documentTimeout.Token);
                    if (local.PdfSearchSupportRequired) throw new InvalidOperationException("The LMS PDF extractor is unavailable.");
                    if (local.Warning is not null) failed++;
                    if (local.Results.Count > 0)
                    {
                        var match = pdf with { MatchedContents = true };
                        matches.Add(match);
                        progress?.Report(new(pdf.ParentPath, DateTimeOffset.UtcNow, match));
                    }
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { failed++; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { failed++; }
                finally { if (File.Exists(localPath)) File.Delete(localPath); }
            }
            var warning = failed == 0 ? response.Warning : string.Join(" ", new[] { response.Warning,
                $"{failed} PDF document(s) could not be searched (unreadable, protected, damaged, too large or timed out). Other files were searched." }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new(response.RootPath, matches.OrderBy(match => match.FullPath, StringComparer.OrdinalIgnoreCase).ToArray(), limitReached, warning);
        }
        finally
        {
            try { if (scratch is not null) Directory.Delete(scratch, true); }
            finally { RemotePdfSearchGate.Release(); }
        }
    }

    private Task<FileSearchResponse> SearchCoreAsync(
        ManagedHost host,
        FileSearchRequest request,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().SearchAsync(
                host,
                request,
                connectionProfile,
                progress,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.SearchAsync(host.DefaultWorkingDirectory, request, progress, cancellationToken);
        }

        var accessRequest = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.SearchAsync(
            host,
            request,
            accessRequest.Username,
            accessRequest.Password,
            accessRequest.PrivateKey,
            accessRequest.PrivateKeyPassphrase,
            accessRequest.PreferStoredCredentials,
            progress,
            cancellationToken);
    }

    public Task<SftpFileContent> ReadFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().ReadFileAsync(host, path, connectionProfile, maxBytes, cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.ReadFileAsync(host.DefaultWorkingDirectory, path, maxBytes, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ReadFileAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            maxBytes,
            cancellationToken);
    }

    public Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().ReadBinaryFileAsync(host, path, connectionProfile, maxBytes, cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.ReadBinaryFileAsync(host.DefaultWorkingDirectory, path, maxBytes, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ReadBinaryFileAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            maxBytes,
            cancellationToken);
    }

    public async Task<Stream> OpenReadStreamAsync(ManagedHost host, string path, ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            var privileged = GetPrivilegedService();
            var first = await privileged.ReadBinaryRangeAsync(host, path, connectionProfile, 0, 262144, cancellationToken);
            return new PrivilegedReadStream(first.SizeBytes, first.ContentBytes, async (offset, count, token) =>
                (await privileged.ReadBinaryRangeAsync(host, path, connectionProfile, offset, count, token)).ContentBytes);
        }
        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
            return await localFileBrowsingService.OpenReadStreamAsync(host.DefaultWorkingDirectory, path, cancellationToken);
        var request = BuildRemoteRequest(host, connectionProfile);
        return await sftpFileBrowsingService.OpenReadStreamAsync(host, path, request.Username, request.Password, request.PrivateKey,
            request.PrivateKeyPassphrase, request.PreferStoredCredentials, cancellationToken);
    }

    private sealed class PrivilegedReadStream(long length, byte[] initial, Func<long, int, CancellationToken, Task<byte[]>> read) : Stream
    {
        private long position;
        private long cachedOffset;
        private byte[] cached = initial;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value >= 0 ? value : throw new IOException("Invalid file seek."); }
        public override long Seek(long offset, SeekOrigin origin) => Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(Math.Min(buffer.Length, 1048576), Math.Max(0, length - position));
            if (count == 0) return 0;
            // TIFF decoders seek repeatedly through tiny header/tag fields. Reuse one bounded
            // read window instead of starting sudo/Python (and potentially SSH) for each field.
            // This is session-local read-ahead, never a full-document download or shared cache.
            if (position < cachedOffset || position >= cachedOffset + cached.Length)
            {
                var bytes = await read(position, (int)Math.Min(Math.Max(count, 262144), length - position), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                cachedOffset = position;
                cached = bytes;
            }
            var available = Math.Min(count, cached.Length - checked((int)(position - cachedOffset)));
            if (available <= 0) throw new EndOfStreamException("The file changed while reading it.");
            cached.AsMemory(checked((int)(position - cachedOffset)), available).CopyTo(buffer);
            position += available;
            return available;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public Task<SftpBinaryFileContent> ReadBinaryRangeAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        long offset,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().ReadBinaryRangeAsync(host, path, connectionProfile, offset, maxBytes, cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.ReadBinaryRangeAsync(host.DefaultWorkingDirectory, path, offset, maxBytes, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ReadBinaryRangeAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            offset,
            maxBytes,
            cancellationToken);
    }

    public Task DownloadFileAsync(
        ManagedHost host,
        string sourcePath,
        string localDestinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().DownloadFileAsync(
                host,
                sourcePath,
                localDestinationPath,
                connectionProfile,
                progress,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.DownloadFileAsync(host.DefaultWorkingDirectory, sourcePath, localDestinationPath, progress, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.DownloadFileAsync(
            host,
            sourcePath,
            localDestinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            progress,
            cancellationToken);
    }

    public Task UploadFileAsync(
        ManagedHost host,
        string localSourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().UploadFileAsync(
                host,
                localSourcePath,
                destinationPath,
                connectionProfile,
                progress,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.UploadFileAsync(host.DefaultWorkingDirectory, localSourcePath, destinationPath, progress, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.UploadFileAsync(
            host,
            localSourcePath,
            destinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            progress,
            cancellationToken);
    }

    public Task<SftpWriteResult> WriteFileAsync(
        ManagedHost host,
        string path,
        string content,
        ManagedHostConnectionProfile connectionProfile,
        bool createDirectories,
        string? encodingName = null,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().WriteFileAsync(
                host,
                path,
                content,
                connectionProfile,
                createDirectories,
                encodingName,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.WriteFileAsync(host.DefaultWorkingDirectory, path, content, createDirectories, encodingName, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.WriteFileAsync(
            host,
            path,
            content,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            createDirectories,
            encodingName,
            cancellationToken);
    }

    public Task<string> CreateDirectoryAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().CreateDirectoryAsync(host, path, connectionProfile, cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.CreateDirectoryAsync(host.DefaultWorkingDirectory, path, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.CreateDirectoryAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task DeleteAsync(
        ManagedHost host,
        string path,
        ManagedHostConnectionProfile connectionProfile,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().DeleteAsync(host, path, connectionProfile, recursive, cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.DeleteAsync(host.DefaultWorkingDirectory, path, recursive, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.DeleteAsync(
            host,
            path,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            recursive,
            cancellationToken);
    }

    public Task<string> CopyAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default,
        IProgress<FileTransferProgress>? progress = null)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().CopyAsync(
                host,
                sourcePath,
                destinationPath,
                connectionProfile,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.CopyAsync(host.DefaultWorkingDirectory, sourcePath, destinationPath, cancellationToken, progress);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.CopyAsync(
            host,
            sourcePath,
            destinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken,
            progress);
    }

    public Task<string> MoveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().MoveAsync(
                host,
                sourcePath,
                destinationPath,
                connectionProfile,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.MoveAsync(host.DefaultWorkingDirectory, sourcePath, destinationPath, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.MoveAsync(
            host,
            sourcePath,
            destinationPath,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<string> CreateZipAsync(
        ManagedHost host,
        string sourcePath,
        string destinationZipPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
        => CreateArchiveAsync(
            host,
            sourcePath,
            destinationZipPath,
            ArchiveFormat.Zip,
            connectionProfile,
            cancellationToken);

    public Task<string> CreateArchiveFromItemsAsync(
        ManagedHost host,
        string baseDirectory,
        IReadOnlyList<string> sourcePaths,
        string destinationArchivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().CreateArchiveFromItemsAsync(
                host,
                baseDirectory,
                sourcePaths,
                destinationArchivePath,
                format,
                connectionProfile,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.CreateArchiveFromItemsAsync(host.DefaultWorkingDirectory, baseDirectory, sourcePaths, destinationArchivePath, format, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.CreateArchiveFromItemsAsync(
            host,
            baseDirectory,
            sourcePaths,
            destinationArchivePath,
            format,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<string> CreateArchiveAsync(
        ManagedHost host,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().CreateArchiveAsync(
                host,
                sourcePath,
                destinationArchivePath,
                format,
                connectionProfile,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.CreateArchiveAsync(host.DefaultWorkingDirectory, sourcePath, destinationArchivePath, format, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.CreateArchiveAsync(
            host,
            sourcePath,
            destinationArchivePath,
            format,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<string> ExtractZipAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
        => ExtractArchiveAsync(
            host,
            archivePath,
            destinationDirectoryPath,
            ArchiveFormat.Zip,
            connectionProfile,
            cancellationToken);

    public Task<string> ExtractArchiveAsync(
        ManagedHost host,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().ExtractArchiveAsync(
                host,
                archivePath,
                destinationDirectoryPath,
                format,
                connectionProfile,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.ExtractArchiveAsync(host.DefaultWorkingDirectory, archivePath, destinationDirectoryPath, format, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ExtractArchiveAsync(
            host,
            archivePath,
            destinationDirectoryPath,
            format,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            cancellationToken);
    }

    public Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        ManagedHost host,
        string archivePath,
        ArchiveFormat format,
        ManagedHostConnectionProfile connectionProfile,
        int maxEntries,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().ListArchiveEntriesAsync(
                host,
                archivePath,
                format,
                connectionProfile,
                maxEntries,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.ListArchiveEntriesAsync(host.DefaultWorkingDirectory, archivePath, format, maxEntries, cancellationToken);
        }

        var request = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.ListArchiveEntriesAsync(
            host,
            archivePath,
            format,
            request.Username,
            request.Password,
            request.PrivateKey,
            request.PrivateKeyPassphrase,
            request.PreferStoredCredentials,
            maxEntries,
            cancellationToken);
    }

    public Task SetOwnershipAndPermissionsAsync(
        ManagedHost host,
        FileOwnershipPermissionsChangeRequest request,
        ManagedHostConnectionProfile connectionProfile,
        CancellationToken cancellationToken = default)
    {
        if (connectionProfile.UseSudo)
        {
            return GetPrivilegedService().SetOwnershipAndPermissionsAsync(
                host,
                request,
                connectionProfile,
                cancellationToken);
        }

        if (ShouldUseLocalFileBrowsing(host, connectionProfile))
        {
            return localFileBrowsingService.SetOwnershipAndPermissionsAsync(host.DefaultWorkingDirectory, request, cancellationToken);
        }

        var accessRequest = BuildRemoteRequest(host, connectionProfile);
        return sftpFileBrowsingService.SetOwnershipAndPermissionsAsync(
            host,
            request,
            accessRequest.Username,
            accessRequest.Password,
            accessRequest.PrivateKey,
            accessRequest.PrivateKeyPassphrase,
            accessRequest.PreferStoredCredentials,
            cancellationToken);
    }

    private ManagedHostConnectionRequest BuildRemoteRequest(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile)
    {
        var secrets = transientConnectionSecretStore.Get(connectionProfile.SecretHandle);
        return new ManagedHostConnectionRequest(
            string.IsNullOrWhiteSpace(connectionProfile.Username) ? host.Username : connectionProfile.Username.Trim(),
            NullIfEmpty(secrets.Password),
            NullIfEmpty(secrets.PrivateKey),
            NullIfEmpty(secrets.PrivateKeyPassphrase),
            connectionProfile.PreferStoredCredentials);
    }

    private static bool ShouldUseLocalFileBrowsing(
        ManagedHost host,
        ManagedHostConnectionProfile connectionProfile) =>
        AiLocalMachine.IsLocalMachine(host.Id) && !connectionProfile.UseSshTransport;

    private PrivilegedFileBrowsingService GetPrivilegedService() =>
        privilegedFileBrowsingService ??
        throw new InvalidOperationException("Privileged file access is not configured.");

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ManagedHostConnectionRequest(
        string Username,
        string? Password,
        string? PrivateKey,
        string? PrivateKeyPassphrase,
        bool PreferStoredCredentials);
}
