// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed record FileThumbnailCacheEntry(byte[] ContentBytes, string ContentType = "image/jpeg");

public sealed record FileThumbnailCacheFlushResult(int RemovedFileCount, long RemovedBytes);

public sealed record FileThumbnailCachePurgeResult(int RemovedFileCount, long RemovedBytes, long RemainingBytes);

public sealed class FileThumbnailCacheService
{
    private static readonly TimeSpan WorkingFileRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan OpportunisticMaintenanceInterval = TimeSpan.FromMinutes(5);
    private const long OpportunisticMaintenanceBytes = 32L * 1024L * 1024L;

    private readonly FileThumbnailCacheOptions options;
    private readonly IFileThumbnailRenderer renderer;
    private readonly ILogger<FileThumbnailCacheService> logger;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim generationGate;
    private readonly SemaphoreSlim maintenanceGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> hostGates = new();
    private long bytesCreatedSinceMaintenance;
    private long nextOpportunisticMaintenanceUtcTicks;

    public FileThumbnailCacheService(
        FileThumbnailCacheOptions options,
        IFileThumbnailRenderer renderer,
        ILogger<FileThumbnailCacheService> logger,
        TimeProvider timeProvider)
    {
        this.options = options;
        this.renderer = renderer;
        this.logger = logger;
        this.timeProvider = timeProvider;
        generationGate = new SemaphoreSlim(options.ConcurrentGenerationLimit, options.ConcurrentGenerationLimit);
        EnsurePrivateDirectory(options.CacheDirectory);
        EnsurePrivateDirectory(WorkingDirectory);
    }

    public int MaxSourceBytes => options.MaxSourceBytes;
    public string CacheDirectory => options.CacheDirectory;

    public async Task<FileThumbnailCacheEntry?> GetOrCreateAsync(
        Guid hostId,
        SftpItem item,
        Func<int, CancellationToken, Task<SftpBinaryFileContent>> sourceLoader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sourceLoader);

        if (item.ItemType is not (SftpItemType.File or SftpItemType.Link) ||
            !FileThumbnailFormatSupport.IsSupportedFileName(item.Name) ||
            item.SizeBytes > options.MaxSourceBytes)
        {
            return null;
        }

        var cachePath = BuildCachePath(hostId, item);
        var cached = await TryReadCachedAsync(cachePath, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var hostGate = hostGates.GetOrAdd(hostId, static _ => new SemaphoreSlim(1, 1));
        await hostGate.WaitAsync(cancellationToken);
        try
        {
            cached = await TryReadCachedAsync(cachePath, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }

            await generationGate.WaitAsync(cancellationToken);
            try
            {
                var source = await sourceLoader(options.MaxSourceBytes, cancellationToken);
                if (source.IsTruncated || source.ContentBytes.Length == 0)
                {
                    return null;
                }

                var hostDirectory = Path.GetDirectoryName(cachePath)!;
                EnsurePrivateDirectory(hostDirectory);
                var operationId = Guid.NewGuid().ToString("N");
                var sourceExtension = NormalizeSourceExtension(item.Name);
                var workingSourcePath = Path.Combine(WorkingDirectory, $"{operationId}{sourceExtension}");
                var workingThumbnailPath = Path.Combine(WorkingDirectory, $"{operationId}.jpg");

                try
                {
                    await File.WriteAllBytesAsync(workingSourcePath, source.ContentBytes, cancellationToken);
                    SetPrivateFilePermissions(workingSourcePath);
                    var rendered = await renderer.RenderJpegAsync(
                        workingSourcePath,
                        workingThumbnailPath,
                        options.MaxDimension,
                        options.GenerationTimeout,
                        cancellationToken);
                    if (!rendered || !File.Exists(workingThumbnailPath))
                    {
                        return null;
                    }

                    File.Move(workingThumbnailPath, cachePath, overwrite: true);
                    SetPrivateFilePermissions(cachePath);
                    Touch(cachePath);
                    var bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                    Interlocked.Add(ref bytesCreatedSinceMaintenance, bytes.LongLength);
                    await RunOpportunisticMaintenanceAsync(cancellationToken);
                    return new FileThumbnailCacheEntry(bytes);
                }
                finally
                {
                    TryDeleteFile(workingSourcePath);
                    TryDeleteFile(workingThumbnailPath);
                }
            }
            finally
            {
                generationGate.Release();
            }
        }
        finally
        {
            hostGate.Release();
        }
    }

    public async Task<FileThumbnailCacheFlushResult> FlushHostAsync(
        Guid hostId,
        CancellationToken cancellationToken = default)
    {
        var hostGate = hostGates.GetOrAdd(hostId, static _ => new SemaphoreSlim(1, 1));
        await hostGate.WaitAsync(cancellationToken);
        try
        {
            var hostDirectory = BuildHostDirectory(hostId);
            var files = SafeEnumerateCacheFiles(hostDirectory).ToArray();
            var removedBytes = files.Sum(GetLength);
            var removedFileCount = files.Length;
            if (Directory.Exists(hostDirectory))
            {
                Directory.Delete(hostDirectory, recursive: true);
            }

            return new FileThumbnailCacheFlushResult(removedFileCount, removedBytes);
        }
        catch (DirectoryNotFoundException)
        {
            return new FileThumbnailCacheFlushResult(0, 0);
        }
        finally
        {
            hostGate.Release();
        }
    }

    public async Task<FileThumbnailCachePurgeResult> PurgeExpiredAndTrimAsync(
        CancellationToken cancellationToken = default)
    {
        await maintenanceGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var expiryThreshold = now - options.Retention;
            var removedCount = 0;
            var removedBytes = 0L;
            var retained = new List<FileInfo>();

            foreach (var path in SafeEnumerateCacheFiles(options.CacheDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = TryGetFileInfo(path);
                if (file is null)
                {
                    continue;
                }

                if (file.LastWriteTimeUtc < expiryThreshold.UtcDateTime)
                {
                    if (TryDeleteFile(file.FullName))
                    {
                        removedCount++;
                        removedBytes += file.Length;
                    }
                }
                else
                {
                    retained.Add(file);
                }
            }

            var remainingBytes = retained.Sum(file => file.Length);
            if (remainingBytes > options.MaxCacheBytes)
            {
                foreach (var file in retained.OrderBy(file => file.LastWriteTimeUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (remainingBytes <= options.MaxCacheBytes)
                    {
                        break;
                    }

                    if (TryDeleteFile(file.FullName))
                    {
                        removedCount++;
                        removedBytes += file.Length;
                        remainingBytes -= file.Length;
                    }
                }
            }

            PurgeAbandonedWorkingFiles(now - WorkingFileRetention);
            RemoveEmptyHostDirectories();
            Interlocked.Exchange(ref bytesCreatedSinceMaintenance, 0);
            Interlocked.Exchange(
                ref nextOpportunisticMaintenanceUtcTicks,
                (now + OpportunisticMaintenanceInterval).UtcTicks);

            return new FileThumbnailCachePurgeResult(removedCount, removedBytes, remainingBytes);
        }
        finally
        {
            maintenanceGate.Release();
        }
    }

    private async Task<FileThumbnailCacheEntry?> TryReadCachedAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(cachePath);
            if (!file.Exists)
            {
                return null;
            }

            if (file.LastWriteTimeUtc < (timeProvider.GetUtcNow() - options.Retention).UtcDateTime)
            {
                TryDeleteFile(cachePath);
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            Touch(cachePath);
            return bytes.Length == 0 ? null : new FileThumbnailCacheEntry(bytes);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "Could not read cached thumbnail {CachePath}.", cachePath);
            return null;
        }
    }

    private async Task RunOpportunisticMaintenanceAsync(CancellationToken cancellationToken)
    {
        var nowTicks = timeProvider.GetUtcNow().UtcTicks;
        var nextTicks = Interlocked.Read(ref nextOpportunisticMaintenanceUtcTicks);
        if (Interlocked.Read(ref bytesCreatedSinceMaintenance) < OpportunisticMaintenanceBytes && nowTicks < nextTicks)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref nextOpportunisticMaintenanceUtcTicks,
                nowTicks + OpportunisticMaintenanceInterval.Ticks,
                nextTicks) != nextTicks)
        {
            return;
        }

        await PurgeExpiredAndTrimAsync(cancellationToken);
    }

    private string BuildCachePath(Guid hostId, SftpItem item)
    {
        var modifiedTicks = item.LastModifiedUtc?.UtcTicks ?? 0;
        var identity = $"{item.FullPath.Trim()}\n{item.SizeBytes}\n{modifiedTicks}\n{options.MaxDimension}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(BuildHostDirectory(hostId), $"{hash}.jpg");
    }

    private string BuildHostDirectory(Guid hostId) =>
        Path.Combine(options.CacheDirectory, hostId.ToString("N"));

    private string WorkingDirectory => Path.Combine(options.CacheDirectory, ".working");

    private static string NormalizeSourceExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrWhiteSpace(extension) &&
               extension.Length <= 10 &&
               extension.Skip(1).All(char.IsLetterOrDigit)
            ? extension.ToLowerInvariant()
            : ".image";
    }

    private static IEnumerable<string> SafeEnumerateCacheFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(root, path).StartsWith($".working{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void PurgeAbandonedWorkingFiles(DateTimeOffset threshold)
    {
        if (!Directory.Exists(WorkingDirectory))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(WorkingDirectory))
            {
                var file = TryGetFileInfo(path);
                if (file is not null && file.LastWriteTimeUtc < threshold.UtcDateTime)
                {
                    TryDeleteFile(file.FullName);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RemoveEmptyHostDirectories()
    {
        if (!Directory.Exists(options.CacheDirectory))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(options.CacheDirectory))
            {
                if (string.Equals(directory, WorkingDirectory, StringComparison.Ordinal) ||
                    Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    continue;
                }

                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static FileInfo? TryGetFileInfo(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static long GetLength(string path) => TryGetFileInfo(path)?.Length ?? 0;

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void SetPrivateFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
