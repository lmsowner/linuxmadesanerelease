using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalFileBrowsingService(
    ILogger<LocalFileBrowsingService> logger,
    ILinuxCommandRunner commandRunner) : ILocalFileBrowsingService
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public Task<IReadOnlyList<SftpItem>> ListItemsAsync(
        string workingDirectory,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, path);
        var directory = new DirectoryInfo(normalizedPath);
        if (!directory.Exists)
        {
            throw new InvalidOperationException($"Directory {normalizedPath} was not found on the local machine.");
        }

        logger.LogInformation("Listing local filesystem items for path {Path}", normalizedPath);

        IReadOnlyList<SftpItem> items = directory
            .EnumerateFileSystemInfos()
            .Select(LocalFileBrowsingSupport.MapItem)
            .OrderByDescending(item => item.ItemType == SftpItemType.Folder)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(items);
    }

    public Task<FileSearchResponse> SearchAsync(
        string workingDirectory,
        FileSearchRequest request,
        IProgress<FileSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRootPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, request.RootPath);
        var rootDirectory = new DirectoryInfo(normalizedRootPath);
        if (!rootDirectory.Exists)
        {
            throw new InvalidOperationException($"Directory {normalizedRootPath} was not found on the local machine.");
        }

        var normalizedRequest = NormalizeSearchRequest(request, normalizedRootPath);
        logger.LogInformation("Searching local filesystem items for path {Path}", normalizedRootPath);

        return Task.Run(
            () => ExecuteSearch(rootDirectory, normalizedRequest, progress, cancellationToken),
            cancellationToken);
    }

    public async Task<SftpFileContent> ReadFileAsync(
        string workingDirectory,
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, path);
        if (Directory.Exists(normalizedPath))
        {
            throw new InvalidOperationException("The requested path is a directory, not a file.");
        }

        var fileInfo = new FileInfo(normalizedPath);
        if (!fileInfo.Exists)
        {
            throw new InvalidOperationException($"File {normalizedPath} was not found on the local machine.");
        }

        var safeMaxBytes = Math.Clamp(maxBytes, 1, 1_048_576);
        await using var stream = fileInfo.OpenRead();
        var buffer = new byte[safeMaxBytes];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        var content = Encoding.UTF8.GetString(buffer, 0, bytesRead);

        return new SftpFileContent(
            normalizedPath,
            content,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
            fileInfo.Length > bytesRead);
    }

    public async Task<SftpBinaryFileContent> ReadBinaryFileAsync(
        string workingDirectory,
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, path);
        if (Directory.Exists(normalizedPath))
        {
            throw new InvalidOperationException("The requested path is a directory, not a file.");
        }

        var fileInfo = new FileInfo(normalizedPath);
        if (!fileInfo.Exists)
        {
            throw new InvalidOperationException($"File {normalizedPath} was not found on the local machine.");
        }

        var safeMaxBytes = Math.Clamp(maxBytes, 1, 67_108_864);
        await using var stream = fileInfo.OpenRead();
        var buffer = new byte[safeMaxBytes];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        var contentBytes = new byte[bytesRead];
        Buffer.BlockCopy(buffer, 0, contentBytes, 0, bytesRead);

        return new SftpBinaryFileContent(
            normalizedPath,
            contentBytes,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
            fileInfo.Length > bytesRead);
    }

    public Task DownloadFileAsync(
        string workingDirectory,
        string sourcePath,
        string localDestinationPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, sourcePath);
        if (!File.Exists(normalizedSourcePath))
        {
            throw new InvalidOperationException($"{normalizedSourcePath} was not found on the local machine.");
        }

        var destinationDirectory = Path.GetDirectoryName(localDestinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        return CopyFileWithProgressAsync(normalizedSourcePath, localDestinationPath, overwrite: false, progress, cancellationToken);
    }

    public Task UploadFileAsync(
        string workingDirectory,
        string localSourcePath,
        string destinationPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(localSourcePath))
        {
            throw new InvalidOperationException($"{localSourcePath} was not found on the local machine.");
        }

        var normalizedDestinationPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, destinationPath);
        EnsureParentDirectoryExists(normalizedDestinationPath);
        EnsureDestinationAvailable(normalizedDestinationPath);
        return CopyFileWithProgressAsync(localSourcePath, normalizedDestinationPath, overwrite: false, progress, cancellationToken);
    }

    public async Task<SftpWriteResult> WriteFileAsync(
        string workingDirectory,
        string path,
        string content,
        bool createDirectories,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, path);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (createDirectories)
            {
                Directory.CreateDirectory(directory);
            }
            else if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException($"Directory {directory} does not exist on the local machine.");
            }
        }

        await File.WriteAllTextAsync(normalizedPath, content, cancellationToken);

        return new SftpWriteResult(normalizedPath, Encoding.UTF8.GetByteCount(content), DateTimeOffset.UtcNow);
    }

    public Task<string> CreateDirectoryAsync(
        string workingDirectory,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, path);
        if (File.Exists(normalizedPath))
        {
            throw new InvalidOperationException($"{normalizedPath} already exists as a file on the local machine.");
        }

        Directory.CreateDirectory(normalizedPath);
        return Task.FromResult(normalizedPath);
    }

    public Task DeleteAsync(
        string workingDirectory,
        string path,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, path);
        EnsureMutablePath(normalizedPath);

        if (Directory.Exists(normalizedPath))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(normalizedPath).Any())
            {
                throw new InvalidOperationException($"{normalizedPath} is not empty.");
            }

            Directory.Delete(normalizedPath, recursive);
            return Task.CompletedTask;
        }

        if (File.Exists(normalizedPath))
        {
            File.Delete(normalizedPath);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException($"{normalizedPath} was not found on the local machine.");
    }

    public Task<string> CopyAsync(
        string workingDirectory,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, sourcePath);
        var normalizedDestinationPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, destinationPath);
        CopyPath(normalizedSourcePath, normalizedDestinationPath);

        return Task.FromResult(normalizedDestinationPath);
    }

    public Task<string> MoveAsync(
        string workingDirectory,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, sourcePath);
        var normalizedDestinationPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, destinationPath);
        EnsureMoveAllowed(normalizedSourcePath, normalizedDestinationPath);

        try
        {
            if (Directory.Exists(normalizedSourcePath))
            {
                Directory.Move(normalizedSourcePath, normalizedDestinationPath);
            }
            else if (File.Exists(normalizedSourcePath))
            {
                File.Move(normalizedSourcePath, normalizedDestinationPath, overwrite: false);
            }
            else
            {
                throw new InvalidOperationException($"{normalizedSourcePath} was not found on the local machine.");
            }
        }
        catch (IOException)
        {
            CopyPath(normalizedSourcePath, normalizedDestinationPath);
            DeletePath(normalizedSourcePath, recursive: true);
        }

        return Task.FromResult(normalizedDestinationPath);
    }

    public Task<string> CreateZipAsync(
        string workingDirectory,
        string sourcePath,
        string destinationZipPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, sourcePath);
        var normalizedDestinationZipPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, destinationZipPath);
        EnsureSourceExists(normalizedSourcePath);
        EnsureDestinationAvailable(normalizedDestinationZipPath);
        EnsureParentDirectoryExists(normalizedDestinationZipPath);

        using var archive = ZipFile.Open(normalizedDestinationZipPath, ZipArchiveMode.Create);
        if (Directory.Exists(normalizedSourcePath))
        {
            AddDirectoryToArchive(archive, new DirectoryInfo(normalizedSourcePath), Path.GetDirectoryName(normalizedSourcePath));
        }
        else
        {
            archive.CreateEntryFromFile(normalizedSourcePath, Path.GetFileName(normalizedSourcePath), CompressionLevel.Fastest);
        }

        return Task.FromResult(normalizedDestinationZipPath);
    }

    public async Task<string> CreateArchiveAsync(
        string workingDirectory,
        string sourcePath,
        string destinationArchivePath,
        ArchiveFormat format,
        CancellationToken cancellationToken = default)
    {
        if (format == ArchiveFormat.Zip)
        {
            return await CreateZipAsync(workingDirectory, sourcePath, destinationArchivePath, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, sourcePath);
        var normalizedDestinationArchivePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, destinationArchivePath);
        EnsureSourceExists(normalizedSourcePath);
        EnsureDestinationAvailable(normalizedDestinationArchivePath);
        EnsureParentDirectoryExists(normalizedDestinationArchivePath);

        if (format == ArchiveFormat.Gzip && Directory.Exists(normalizedSourcePath))
        {
            format = ArchiveFormat.TarGzip;
        }

        switch (format)
        {
            case ArchiveFormat.Gzip:
                await CreateGzipArchiveAsync(normalizedSourcePath, normalizedDestinationArchivePath, cancellationToken);
                break;
            case ArchiveFormat.TarGzip:
                await CreateTarGzipArchiveAsync(normalizedSourcePath, normalizedDestinationArchivePath, cancellationToken);
                break;
            case ArchiveFormat.SevenZip:
                await CreateSevenZipArchiveAsync(normalizedSourcePath, normalizedDestinationArchivePath, cancellationToken);
                break;
            case ArchiveFormat.Rar:
                await CreateRarArchiveAsync(normalizedSourcePath, normalizedDestinationArchivePath, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} compression is not supported.");
        }

        return normalizedDestinationArchivePath;
    }

    public Task<string> ExtractZipAsync(
        string workingDirectory,
        string archivePath,
        string destinationDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArchivePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, archivePath);
        var normalizedDestinationDirectoryPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, destinationDirectoryPath);
        EnsureMutablePath(normalizedDestinationDirectoryPath);

        if (!File.Exists(normalizedArchivePath))
        {
            throw new InvalidOperationException($"{normalizedArchivePath} was not found on the local machine.");
        }

        if (Directory.Exists(normalizedDestinationDirectoryPath) &&
            Directory.EnumerateFileSystemEntries(normalizedDestinationDirectoryPath).Any())
        {
            throw new InvalidOperationException($"{normalizedDestinationDirectoryPath} already exists and is not empty.");
        }

        Directory.CreateDirectory(normalizedDestinationDirectoryPath);
        ZipFile.ExtractToDirectory(normalizedArchivePath, normalizedDestinationDirectoryPath, overwriteFiles: false);
        return Task.FromResult(normalizedDestinationDirectoryPath);
    }

    public async Task<string> ExtractArchiveAsync(
        string workingDirectory,
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        CancellationToken cancellationToken = default)
    {
        if (format == ArchiveFormat.Zip)
        {
            return await ExtractZipAsync(workingDirectory, archivePath, destinationDirectoryPath, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArchivePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, archivePath);
        var normalizedDestinationDirectoryPath = PrepareExtractDestination(workingDirectory, destinationDirectoryPath);

        if (!File.Exists(normalizedArchivePath))
        {
            throw new InvalidOperationException($"{normalizedArchivePath} was not found on the local machine.");
        }

        Directory.CreateDirectory(normalizedDestinationDirectoryPath);
        switch (format)
        {
            case ArchiveFormat.Gzip:
                await ExtractGzipArchiveAsync(normalizedArchivePath, normalizedDestinationDirectoryPath, cancellationToken);
                break;
            case ArchiveFormat.TarGzip:
                await ExtractTarGzipArchiveAsync(normalizedArchivePath, normalizedDestinationDirectoryPath, cancellationToken);
                break;
            case ArchiveFormat.SevenZip:
            case ArchiveFormat.Rar:
                await ExtractSevenZipCompatibleArchiveAsync(normalizedArchivePath, normalizedDestinationDirectoryPath, format, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} extraction is not supported.");
        }

        return normalizedDestinationDirectoryPath;
    }

    public async Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(
        string workingDirectory,
        string archivePath,
        ArchiveFormat format,
        int maxEntries,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArchivePath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, archivePath);
        if (!File.Exists(normalizedArchivePath))
        {
            throw new InvalidOperationException($"{normalizedArchivePath} was not found on the local machine.");
        }

        var safeMaxEntries = Math.Clamp(maxEntries, 1, 2_000);
        return format switch
        {
            ArchiveFormat.Zip => ListZipEntries(normalizedArchivePath, safeMaxEntries),
            ArchiveFormat.Gzip => ListGzipEntries(normalizedArchivePath),
            ArchiveFormat.TarGzip => ParseSimpleArchiveEntryList(
                (await RunLocalArchiveCommandAsync(
                    "tar",
                    ["-tzf", normalizedArchivePath],
                    $"List {normalizedArchivePath}",
                    cancellationToken)).StandardOutput,
                safeMaxEntries),
            ArchiveFormat.SevenZip or ArchiveFormat.Rar => ParseSevenZipTechnicalList(
                (await RunLocalArchiveShellCommandAsync(
                    BuildSevenZipListCommand(normalizedArchivePath),
                    $"List {normalizedArchivePath}",
                    cancellationToken)).StandardOutput,
                safeMaxEntries),
            _ => throw new NotSupportedException($"{ArchiveFormatSupport.GetDisplayName(format)} preview is not supported.")
        };
    }

    public async Task SetOwnershipAndPermissionsAsync(
        string workingDirectory,
        FileOwnershipPermissionsChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, request.Path);
        EnsureMutablePath(normalizedPath);
        EnsureSourceExists(normalizedPath);

        var isDirectory = Directory.Exists(normalizedPath);
        var recursive = request.Recursive && isDirectory;
        var ownershipSpec = BuildOwnershipSpec(request.OwnerName, request.GroupName);
        var permissionsOctal = NormalizePermissionMode(request.PermissionsOctal);

        if (ownershipSpec is null && permissionsOctal is null)
        {
            throw new InvalidOperationException("Provide an owner, group, or octal permissions value before applying changes.");
        }

        if (ownershipSpec is not null)
        {
            await ExecuteMutationCommandAsync(
                "chown",
                recursive ? ["-R", ownershipSpec, "--", normalizedPath] : [ownershipSpec, "--", normalizedPath],
                $"Update local ownership for {normalizedPath}",
                cancellationToken);
        }

        if (permissionsOctal is not null)
        {
            await ExecuteMutationCommandAsync(
                "chmod",
                recursive ? ["-R", permissionsOctal, normalizedPath] : [permissionsOctal, normalizedPath],
                $"Update local permissions for {normalizedPath}",
                cancellationToken);
        }
    }

    private static void CopyPath(string sourcePath, string destinationPath)
    {
        EnsureCopyAllowed(sourcePath, destinationPath);

        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(new DirectoryInfo(sourcePath), new DirectoryInfo(destinationPath));
            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"{sourcePath} was not found on the local machine.");
        }

        EnsureParentDirectoryExists(destinationPath);
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static void DeletePath(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void CopyDirectory(DirectoryInfo sourceDirectory, DirectoryInfo destinationDirectory)
    {
        if (!sourceDirectory.Exists)
        {
            throw new InvalidOperationException($"{sourceDirectory.FullName} was not found on the local machine.");
        }

        if (destinationDirectory.Exists)
        {
            throw new InvalidOperationException($"{destinationDirectory.FullName} already exists on the local machine.");
        }

        destinationDirectory.Create();

        foreach (var file in sourceDirectory.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destinationDirectory.FullName, file.Name), overwrite: false);
        }

        foreach (var directory in sourceDirectory.EnumerateDirectories())
        {
            CopyDirectory(directory, new DirectoryInfo(Path.Combine(destinationDirectory.FullName, directory.Name)));
        }
    }

    private static void AddDirectoryToArchive(ZipArchive archive, DirectoryInfo directory, string? baseDirectory)
    {
        var entryRoot = Path.GetRelativePath(baseDirectory ?? directory.Parent?.FullName ?? directory.FullName, directory.FullName)
            .Replace('\\', '/')
            .TrimEnd('/');
        if (!directory.EnumerateFileSystemInfos().Any())
        {
            archive.CreateEntry(entryRoot + "/");
            return;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo childDirectory)
            {
                AddDirectoryToArchive(archive, childDirectory, baseDirectory);
                continue;
            }

            var entryName = Path.GetRelativePath(baseDirectory ?? directory.Parent?.FullName ?? directory.FullName, entry.FullName)
                .Replace('\\', '/');
            archive.CreateEntryFromFile(entry.FullName, entryName, CompressionLevel.Fastest);
        }
    }

    private static async Task CreateGzipArchiveAsync(
        string sourcePath,
        string destinationArchivePath,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(sourcePath))
        {
            throw new InvalidOperationException("Gzip can only compress a single file. Use tar.gz for folders.");
        }

        await using var sourceStream = File.OpenRead(sourcePath);
        await using var destinationStream = File.Create(destinationArchivePath);
        await using var gzipStream = new GZipStream(destinationStream, CompressionLevel.Fastest);
        await sourceStream.CopyToAsync(gzipStream, cancellationToken);
    }

    private async Task CreateTarGzipArchiveAsync(
        string sourcePath,
        string destinationArchivePath,
        CancellationToken cancellationToken)
    {
        var (sourceParent, sourceName) = SplitSourcePath(sourcePath);
        await ExecuteLocalArchiveCommandAsync(
            "tar",
            ["-czf", destinationArchivePath, "-C", sourceParent, sourceName],
            $"Create tar.gz archive {destinationArchivePath}",
            cancellationToken);
    }

    private async Task CreateSevenZipArchiveAsync(
        string sourcePath,
        string destinationArchivePath,
        CancellationToken cancellationToken)
    {
        var (sourceParent, sourceName) = SplitSourcePath(sourcePath);
        await ExecuteLocalArchiveShellCommandAsync(
            BuildSevenZipCreateCommand(sourceParent, sourceName, destinationArchivePath),
            $"Create 7z archive {destinationArchivePath}",
            cancellationToken);
    }

    private async Task CreateRarArchiveAsync(
        string sourcePath,
        string destinationArchivePath,
        CancellationToken cancellationToken)
    {
        var (sourceParent, sourceName) = SplitSourcePath(sourcePath);
        await ExecuteLocalArchiveShellCommandAsync(
            BuildRarCreateCommand(sourceParent, sourceName, destinationArchivePath),
            $"Create RAR archive {destinationArchivePath}",
            cancellationToken);
    }

    private static async Task ExtractGzipArchiveAsync(
        string archivePath,
        string destinationDirectoryPath,
        CancellationToken cancellationToken)
    {
        var outputFileName = ResolveGzipOutputFileName(archivePath);
        var outputPath = Path.Combine(destinationDirectoryPath, outputFileName);
        EnsureDestinationAvailable(outputPath);

        await using var sourceStream = File.OpenRead(archivePath);
        await using var gzipStream = new GZipStream(sourceStream, CompressionMode.Decompress);
        await using var destinationStream = File.Create(outputPath);
        await gzipStream.CopyToAsync(destinationStream, cancellationToken);
    }

    private async Task ExtractTarGzipArchiveAsync(
        string archivePath,
        string destinationDirectoryPath,
        CancellationToken cancellationToken)
    {
        await ExecuteLocalArchiveCommandAsync(
            "tar",
            ["-xzf", archivePath, "-C", destinationDirectoryPath],
            $"Extract tar.gz archive {archivePath}",
            cancellationToken);
    }

    private async Task ExtractSevenZipCompatibleArchiveAsync(
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format,
        CancellationToken cancellationToken)
    {
        await ExecuteLocalArchiveShellCommandAsync(
            BuildSevenZipExtractCommand(archivePath, destinationDirectoryPath, format),
            $"Extract {ArchiveFormatSupport.GetDisplayName(format)} archive {archivePath}",
            cancellationToken);
    }

    private static IReadOnlyList<ArchiveEntry> ListZipEntries(string archivePath, int maxEntries)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries
            .Take(maxEntries)
            .Select(entry =>
            {
                var fullName = entry.FullName.Replace('\\', '/');
                var isDirectory = fullName.EndsWith("/", StringComparison.Ordinal);
                var trimmedName = fullName.TrimEnd('/');
                var name = Path.GetFileName(trimmedName);
                return new ArchiveEntry(
                    string.IsNullOrWhiteSpace(name) ? trimmedName : name,
                    fullName,
                    isDirectory ? SftpItemType.Folder : SftpItemType.File,
                    isDirectory ? 0 : entry.Length,
                    entry.LastWriteTime == DateTimeOffset.MinValue ? null : entry.LastWriteTime);
            })
            .ToArray();
    }

    private static IReadOnlyList<ArchiveEntry> ListGzipEntries(string archivePath) =>
    [
        new ArchiveEntry(
            ResolveGzipOutputFileName(archivePath),
            ResolveGzipOutputFileName(archivePath),
            SftpItemType.File,
            new FileInfo(archivePath).Length,
            null)
    ];

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

    private string PrepareExtractDestination(string workingDirectory, string destinationDirectoryPath)
    {
        var normalizedDestinationDirectoryPath = LocalFileBrowsingSupport.NormalizePath(workingDirectory, destinationDirectoryPath);
        EnsureMutablePath(normalizedDestinationDirectoryPath);

        if (Directory.Exists(normalizedDestinationDirectoryPath) &&
            Directory.EnumerateFileSystemEntries(normalizedDestinationDirectoryPath).Any())
        {
            throw new InvalidOperationException($"{normalizedDestinationDirectoryPath} already exists and is not empty.");
        }

        EnsureParentDirectoryExists(normalizedDestinationDirectoryPath);
        return normalizedDestinationDirectoryPath;
    }

    private async Task ExecuteLocalArchiveCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await RunLocalArchiveCommandAsync(fileName, arguments, description, cancellationToken);
        ThrowIfCommandFailed(result, description);
    }

    private async Task<LinuxCommandResult> RunLocalArchiveCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                fileName,
                arguments,
                RequiresSudo: false,
                Timeout: TimeSpan.FromMinutes(30),
                description)
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        ThrowIfCommandFailed(result, description);
        return result;
    }

    private async Task ExecuteLocalArchiveShellCommandAsync(
        string commandText,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await RunLocalArchiveShellCommandAsync(commandText, description, cancellationToken);
        ThrowIfCommandFailed(result, description);
    }

    private async Task<LinuxCommandResult> RunLocalArchiveShellCommandAsync(
        string commandText,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "sh",
                ["-c", commandText],
                RequiresSudo: false,
                Timeout: TimeSpan.FromMinutes(30),
                description)
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        ThrowIfCommandFailed(result, description);
        return result;
    }

    private static void ThrowIfCommandFailed(LinuxCommandResult result, string description)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? string.IsNullOrWhiteSpace(result.StandardOutput)
                ? $"{description} failed with exit code {result.ExitCode}."
                : result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException(detail);
    }

    private static (string ParentDirectory, string FileName) SplitSourcePath(string sourcePath)
    {
        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("The selected path cannot be archived.");
        }

        return (parent, name);
    }

    private static string ResolveGzipOutputFileName(string archivePath)
    {
        var fileName = Path.GetFileName(archivePath);
        if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^3];
        }

        return string.IsNullOrWhiteSpace(fileName) ? "content" : fileName;
    }

    private static string GetArchiveEntryName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    private static string BuildSevenZipCreateCommand(
        string sourceParent,
        string sourceName,
        string destinationArchivePath) =>
        "if command -v 7z >/dev/null 2>&1; then LMS_7Z=7z; " +
        "elif command -v 7zz >/dev/null 2>&1; then LMS_7Z=7zz; " +
        "else echo '7z or 7zz is required for 7Zip support.' >&2; exit 127; fi\n" +
        $"cd -- {QuoteShellArgument(sourceParent)} && \"$LMS_7Z\" a -t7z {QuoteShellArgument(destinationArchivePath)} {QuoteShellArgument(sourceName)}";

    private static string BuildSevenZipExtractCommand(
        string archivePath,
        string destinationDirectoryPath,
        ArchiveFormat format) =>
        "if command -v 7z >/dev/null 2>&1; then LMS_7Z=7z; " +
        "elif command -v 7zz >/dev/null 2>&1; then LMS_7Z=7zz; " +
        $"else echo '7z or 7zz is required for {ArchiveFormatSupport.GetDisplayName(format)} extraction.' >&2; exit 127; fi\n" +
        $"\"$LMS_7Z\" x -y -o{QuoteShellArgument(destinationDirectoryPath)} {QuoteShellArgument(archivePath)}";

    private static string BuildSevenZipListCommand(string archivePath) =>
        "if command -v 7z >/dev/null 2>&1; then LMS_7Z=7z; " +
        "elif command -v 7zz >/dev/null 2>&1; then LMS_7Z=7zz; " +
        "else echo '7z or 7zz is required to preview this archive.' >&2; exit 127; fi\n" +
        $"\"$LMS_7Z\" l -slt {QuoteShellArgument(archivePath)}";

    private static string BuildRarCreateCommand(
        string sourceParent,
        string sourceName,
        string destinationArchivePath) =>
        "command -v rar >/dev/null 2>&1 || { echo 'rar is required for RAR compression.' >&2; exit 127; }\n" +
        $"cd -- {QuoteShellArgument(sourceParent)} && rar a -r {QuoteShellArgument(destinationArchivePath)} {QuoteShellArgument(sourceName)}";

    private static string QuoteShellArgument(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void EnsureSourceExists(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException($"{path} was not found on the local machine.");
        }
    }

    private static void EnsureDestinationAvailable(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new InvalidOperationException($"{path} already exists on the local machine.");
        }
    }

    private static void EnsureParentDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || Directory.Exists(directory))
        {
            return;
        }

        throw new InvalidOperationException($"Directory {directory} does not exist on the local machine.");
    }

    private static void EnsureMutablePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetFullPath(Path.GetPathRoot(fullPath) ?? fullPath);
        if (string.Equals(fullPath, rootPath, PathComparison))
        {
            throw new InvalidOperationException("The filesystem root cannot be modified.");
        }
    }

    private static void EnsureCopyAllowed(string sourcePath, string destinationPath)
    {
        EnsureSourceExists(sourcePath);
        EnsureDestinationAvailable(destinationPath);
        EnsureParentDirectoryExists(destinationPath);

        if (string.Equals(sourcePath, destinationPath, PathComparison))
        {
            throw new InvalidOperationException("The source and destination paths are the same.");
        }

        if (Directory.Exists(sourcePath) && IsChildPath(sourcePath, destinationPath))
        {
            throw new InvalidOperationException("A directory cannot be copied into itself.");
        }
    }

    private static void EnsureMoveAllowed(string sourcePath, string destinationPath)
    {
        EnsureMutablePath(sourcePath);
        EnsureCopyAllowed(sourcePath, destinationPath);
    }

    private static bool IsChildPath(string parentPath, string childPath)
    {
        var normalizedParent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedChild = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedChild.StartsWith(normalizedParent + Path.DirectorySeparatorChar, PathComparison) ||
            normalizedChild.StartsWith(normalizedParent + Path.AltDirectorySeparatorChar, PathComparison);
    }

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

    private static FileSearchRequest NormalizeSearchRequest(FileSearchRequest request, string normalizedRootPath)
    {
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

    private static FileSearchResponse ExecuteSearch(
        DirectoryInfo rootDirectory,
        FileSearchRequest request,
        IProgress<FileSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var nameMatcher = BuildNameMatcher(request.NamePattern, request.CaseInsensitive);
        var results = new List<FileSearchMatch>();
        var directories = new Stack<DirectoryInfo>();
        directories.Push(rootDirectory);
        var limitReached = false;
        var lastProgressAtUtc = DateTimeOffset.MinValue;

        while (directories.Count > 0 && !limitReached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = directories.Pop();
            ReportSearchProgress(progress, currentDirectory.FullName, ref lastProgressAtUtc);
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = currentDirectory.EnumerateFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SftpItem mappedItem;
                try
                {
                    mappedItem = LocalFileBrowsingSupport.MapItem(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (entry is DirectoryInfo childDirectory &&
                    mappedItem.ItemType == SftpItemType.Folder &&
                    !IsSymbolicLink(entry))
                {
                    directories.Push(childDirectory);
                }

                if (!MatchesSearchCriteria(entry, mappedItem, request, nameMatcher, cancellationToken))
                {
                    continue;
                }

                results.Add(new FileSearchMatch(
                    mappedItem.Name,
                    mappedItem.FullPath,
                    GetParentDirectoryPath(mappedItem.FullPath),
                    mappedItem.ItemType,
                    mappedItem.SizeBytes,
                    mappedItem.LastModifiedUtc,
                    MatchedContents: request.RequiresContentScan,
                    mappedItem.LinkTarget));

                progress?.Report(new FileSearchProgress(
                    currentDirectory.FullName,
                    DateTimeOffset.UtcNow,
                    results[^1]));

                if (results.Count >= request.MaxResults)
                {
                    limitReached = true;
                    break;
                }
            }
        }

        var orderedResults = results
            .OrderBy(result => result.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FileSearchResponse(request.RootPath, orderedResults, limitReached);
    }

    private static void ReportSearchProgress(
        IProgress<FileSearchProgress>? progress,
        string currentPath,
        ref DateTimeOffset lastProgressAtUtc)
    {
        if (progress is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (lastProgressAtUtc != DateTimeOffset.MinValue &&
            now - lastProgressAtUtc < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        progress.Report(new FileSearchProgress(currentPath, now));
        lastProgressAtUtc = now;
    }

    private static bool MatchesSearchCriteria(
        FileSystemInfo entry,
        SftpItem mappedItem,
        FileSearchRequest request,
        Func<string, bool> nameMatcher,
        CancellationToken cancellationToken)
    {
        var isFolder = mappedItem.ItemType == SftpItemType.Folder;
        var includeEntry = isFolder ? request.IncludeFolders : request.IncludeFiles;
        if (!includeEntry)
        {
            return false;
        }

        if (!nameMatcher(mappedItem.Name))
        {
            return false;
        }

        if (request.ModifiedFromUtc.HasValue &&
            mappedItem.LastModifiedUtc.HasValue &&
            mappedItem.LastModifiedUtc.Value < request.ModifiedFromUtc.Value)
        {
            return false;
        }

        if (request.ModifiedToUtc.HasValue &&
            mappedItem.LastModifiedUtc.HasValue &&
            mappedItem.LastModifiedUtc.Value > request.ModifiedToUtc.Value)
        {
            return false;
        }

        if (request.CreatedFromUtc.HasValue || request.CreatedToUtc.HasValue)
        {
            var createdAtUtc = GetUtcTimestamp(entry.CreationTimeUtc);
            if (request.CreatedFromUtc.HasValue && createdAtUtc.HasValue && createdAtUtc.Value < request.CreatedFromUtc.Value)
            {
                return false;
            }

            if (request.CreatedToUtc.HasValue && createdAtUtc.HasValue && createdAtUtc.Value > request.CreatedToUtc.Value)
            {
                return false;
            }
        }

        if (request.AccessedFromUtc.HasValue || request.AccessedToUtc.HasValue)
        {
            var accessedAtUtc = GetUtcTimestamp(entry.LastAccessTimeUtc);
            if (request.AccessedFromUtc.HasValue && accessedAtUtc.HasValue && accessedAtUtc.Value < request.AccessedFromUtc.Value)
            {
                return false;
            }

            if (request.AccessedToUtc.HasValue && accessedAtUtc.HasValue && accessedAtUtc.Value > request.AccessedToUtc.Value)
            {
                return false;
            }
        }

        if (!isFolder && request.MinimumSizeBytes.HasValue && mappedItem.SizeBytes < request.MinimumSizeBytes.Value)
        {
            return false;
        }

        if (!isFolder && request.MaximumSizeBytes.HasValue && mappedItem.SizeBytes > request.MaximumSizeBytes.Value)
        {
            return false;
        }

        if (isFolder)
        {
            return !request.MinimumSizeBytes.HasValue &&
                   !request.MaximumSizeBytes.HasValue &&
                   !request.RequiresContentScan;
        }

        if (!request.RequiresContentScan)
        {
            return true;
        }

        if (entry is not FileInfo file)
        {
            return false;
        }

        return FileContainsText(file, request.ContainsText!, request.CaseInsensitive, cancellationToken);
    }

    private static bool FileContainsText(
        FileInfo file,
        string containsText,
        bool caseInsensitive,
        CancellationToken cancellationToken)
    {
        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var carryover = string.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return carryover.Contains(containsText, comparison);
            }

            var chunk = carryover + new string(buffer, 0, read);
            if (chunk.Contains('\0'))
            {
                return false;
            }

            if (chunk.Contains(containsText, comparison))
            {
                return true;
            }

            var carryLength = Math.Min(Math.Max(containsText.Length - 1, 0), chunk.Length);
            carryover = carryLength == 0 ? string.Empty : chunk[^carryLength..];
        }
    }

    private static Func<string, bool> BuildNameMatcher(string? namePattern, bool caseInsensitive)
    {
        var trimmed = namePattern?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return static _ => true;
        }

        if (trimmed.Contains('*', StringComparison.Ordinal) ||
            trimmed.Contains('?', StringComparison.Ordinal))
        {
            var options = RegexOptions.CultureInvariant | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
            var regex = new Regex(
                "^" + Regex.Escape(trimmed).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
                options);
            return name => regex.IsMatch(name);
        }

        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return name => name.Contains(trimmed, comparison);
    }

    private static bool IsSymbolicLink(FileSystemInfo item)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(item.LinkTarget);
        }
        catch
        {
            return false;
        }
    }

    private static string GetParentDirectoryPath(string fullPath)
    {
        var parentDirectory = Path.GetDirectoryName(fullPath);
        return string.IsNullOrWhiteSpace(parentDirectory)
            ? Path.GetPathRoot(fullPath) ?? fullPath
            : parentDirectory;
    }

    private static DateTimeOffset? GetUtcTimestamp(DateTime value) =>
        value == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private static async Task CopyFileWithProgressAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        progress?.Report(new FileTransferProgress(0, sourceInfo.Length));

        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        await using var destinationStream = new FileStream(destinationPath, mode, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);

        var buffer = new byte[1024 * 128];
        long totalBytesTransferred = 0;

        while (true)
        {
            var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesTransferred += bytesRead;
            progress?.Report(new FileTransferProgress(totalBytesTransferred, sourceInfo.Length));
        }

        await destinationStream.FlushAsync(cancellationToken);
    }

    private async Task ExecuteMutationCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                fileName,
                arguments,
                RequiresSudo: true,
                Timeout: TimeSpan.FromSeconds(20),
                description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? string.IsNullOrWhiteSpace(result.StandardOutput)
                ? $"{description} failed on the LMS host."
                : result.StandardOutput.Trim()
            : result.StandardError.Trim();

        throw new InvalidOperationException(detail);
    }
}
