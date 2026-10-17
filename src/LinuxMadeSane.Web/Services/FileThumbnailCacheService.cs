// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed record FileThumbnailCacheEntry(byte[] ContentBytes, string ContentType = "image/jpeg", int PageCount = 1, int PageIndex = 0, bool PageCountComplete = true);

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
    private readonly SemaphoreSlim documentGenerationGate = new(1, 1);
    private int pendingDocumentGenerations;
    public bool HasPendingDocumentGenerations => Volatile.Read(ref pendingDocumentGenerations) > 0;
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

    public int MaxDimension => options.MaxDimension;
    public int MaxDocumentSourceBytes => options.MaxDocumentSourceBytes;

    public async Task<FileStream> OpenDocumentSourceAsync(
        Func<string, IProgress<FileTransferProgress>, CancellationToken, Task> download, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(WorkingDirectory, Guid.NewGuid().ToString("N") + ".document");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new DocumentBudgetProgress(update =>
        {
            if (update.BytesTransferred > MaxDocumentSourceBytes || update.TotalBytes > MaxDocumentSourceBytes)
            {
                try { budget.Cancel(); } catch (ObjectDisposedException) { }
            }
        });
        try
        {
            await download(path, progress, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
            var size = new FileInfo(path).Length;
            if (size > MaxDocumentSourceBytes) throw new InvalidOperationException($"Document thumbnail limit: {MaxDocumentSourceBytes / 1048576} MB.");
            if (size == 0) throw new InvalidOperationException("The document could not be read completely.");
            SetPrivateFilePermissions(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            TryDeleteFile(path);
            throw new InvalidOperationException($"Document thumbnail limit: {MaxDocumentSourceBytes / 1048576} MB.");
        }
        catch { TryDeleteFile(path); throw; }
    }

    private sealed class DocumentBudgetProgress(Action<FileTransferProgress> report) : IProgress<FileTransferProgress>
    {
        public void Report(FileTransferProgress value) => report(value);
    }

    public async Task CompleteDocumentPageCountAsync(Guid hostId, SftpItem item, int pageCount, string accessScope,
        CancellationToken cancellationToken = default)
    {
        if (pageCount is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var scopeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accessScope)));
        var pagePath = BuildCachePath(hostId, item)[..^4] + $"-document-{scopeHash}-0.jpg";
        if (!File.Exists(pagePath)) return;
        EnsurePrivateDirectory(WorkingDirectory);
        var temp = Path.Combine(WorkingDirectory, Guid.NewGuid().ToString("N") + ".pages");
        try
        {
            await File.WriteAllTextAsync(temp, pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            SetPrivateFilePermissions(temp);
            if (File.Exists(pagePath)) File.Move(temp, pagePath + ".pages", true);
        }
        finally { TryDeleteFile(temp); }
    }

    public async Task<FileThumbnailCacheEntry?> GetDocumentPageAsync(Guid hostId, SftpItem item, int pageIndex,
        Func<CancellationToken, Task<FileThumbnailCacheEntry>> render, CancellationToken cancellationToken = default, string accessScope = "")
    {
        if (item.ItemType is not (SftpItemType.File or SftpItemType.Link) || !FileThumbnailFormatSupport.IsPagedDocument(item.Name) || pageIndex < 0)
            return null;
        var scopeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accessScope)));
        var cachePath = BuildCachePath(hostId, item)[..^4] + $"-document-{scopeHash}-{pageIndex}.jpg";
        async Task<FileThumbnailCacheEntry?> Read()
        {
            var entry = await TryReadCachedAsync(cachePath, cancellationToken);
            if (entry is null) return null;
            try
            {
                var pages = int.Parse(await File.ReadAllTextAsync(cachePath + ".pages", cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                return pages != int.MinValue && Math.Abs(pages) > pageIndex ? entry with { PageCount = Math.Abs(pages), PageIndex = pageIndex, PageCountComplete = pages > 0 } : null;
            }
            catch (Exception ex) when (ex is IOException or FormatException or OverflowException) { return null; }
        }
        var cached = await Read();
        if (cached is not null) return cached;
        var hostGate = hostGates.GetOrAdd(hostId, static _ => new SemaphoreSlim(1, 1));
        Interlocked.Increment(ref pendingDocumentGenerations);
        try { await hostGate.WaitAsync(cancellationToken); }
        catch { Interlocked.Decrement(ref pendingDocumentGenerations); throw; }
        try
        {
            cached = await Read();
            if (cached is not null) return cached;
            await documentGenerationGate.WaitAsync(cancellationToken);
            try
            {
                var result = await render(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.PageIndex != pageIndex || result.PageCount <= pageIndex || result.PageCount > 1_000_000 ||
                    result.ContentBytes.Length is < 2 or > 2_097_152 || result.ContentBytes[0] != 0xff || result.ContentBytes[1] != 0xd8)
                    throw new InvalidOperationException("The document thumbnail renderer returned invalid page data.");
                EnsurePrivateDirectory(Path.GetDirectoryName(cachePath)!);
                var temp = Path.Combine(WorkingDirectory, Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    await File.WriteAllBytesAsync(temp, result.ContentBytes, cancellationToken);
                    SetPrivateFilePermissions(temp);
                    await File.WriteAllTextAsync(cachePath + ".pages", (result.PageCountComplete ? result.PageCount : -result.PageCount).ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                    SetPrivateFilePermissions(cachePath + ".pages");
                    File.Move(temp, cachePath, true); Touch(cachePath);
                }
                finally { TryDeleteFile(temp); }
                Interlocked.Add(ref bytesCreatedSinceMaintenance, result.ContentBytes.Length);
                await RunOpportunisticMaintenanceAsync(cancellationToken);
                return result;
            }
            finally { documentGenerationGate.Release(); }
        }
        finally { hostGate.Release(); Interlocked.Decrement(ref pendingDocumentGenerations); }
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
            if (File.Exists(path + ".pages")) File.Delete(path + ".pages");
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
