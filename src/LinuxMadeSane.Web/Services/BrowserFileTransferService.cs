// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Web.Services;

// Guardrail: browser upload/download orchestration belongs here so pages keep using the
// existing file-action queue and shared host file-access boundary instead of
// reimplementing binary transfer orchestration in UI components.
public sealed partial class BrowserFileTransferService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BrowserUploadTransfer> uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserDownloadTransfer> downloads = new(StringComparer.Ordinal);
    private readonly string transferRoot = LmsTemporaryPaths.Combine("browser-transfers");

    // URLs are opaque capabilities registered by an already-authorized file workspace.
    // No client-supplied host, path or credentials are accepted by the HTTP endpoint.
    private readonly ConcurrentDictionary<string, PreviewReadSession> previews = new(StringComparer.Ordinal);

    public string RegisterPreview(Func<CancellationToken, Task<Stream>> openStream)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        previews[token] = new PreviewReadSession(openStream);
        return $"/internal/file-previews/{token}";
    }

    public void RevokePreview(string? url)
    {
        if (!string.IsNullOrEmpty(url) && previews.TryRemove(url.Split('/')[^1], out var session))
            _ = session.DisposeAsync().AsTask();
    }

    public Task<Stream>? OpenPreviewAsync(string token, CancellationToken cancellationToken) =>
        previews.TryGetValue(token, out var session) ? session.OpenAsync(cancellationToken) : null;

    public async ValueTask DisposeAsync()
    {
        desktopCleanup.Dispose();
        foreach (var entry in desktopDownloads.Values) entry.Dispose();
        foreach (var token in uploads.Keys) await CancelUploadAsync(token, "LMS is stopping.");
        foreach (var key in previews.Keys)
            if (previews.TryRemove(key, out var session)) await session.DisposeAsync();
    }

    public static void MapPreviewEndpoints(WebApplication app)
    {
        MapDesktopDownloadEndpoints(app);
        app.MapMethods("/internal/file-previews/{token}", ["GET", "HEAD"], async (
            HttpContext context, string token, BrowserFileTransferService transfers) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var pending = transfers.OpenPreviewAsync(token, context.RequestAborted);
            if (pending is null) return Results.NotFound();
            try
            {
                var stream = await pending;
                return Results.Stream(stream, "application/pdf", enableRangeProcessing: true);
            }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (DirectoryNotFoundException) { return Results.NotFound(); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        });
    }

    public BrowserFileTransferService()
    {
        Directory.CreateDirectory(transferRoot);
        desktopCleanup = new Timer(_ => ExpireDesktopDownloads(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public BrowserUploadRegistration RegisterUpload(
        string workspaceId,
        Guid jobId,
        Guid itemId,
        string fileName,
        long totalBytes)
    {
        var token = Guid.NewGuid().ToString("N");
        var sanitizedFileName = SanitizeFileName(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        var transfer = new BrowserUploadTransfer(
            token,
            workspaceId,
            jobId,
            itemId,
            sanitizedFileName,
            totalBytes);

        uploads[token] = transfer;
        return new BrowserUploadRegistration(token, sanitizedFileName, totalBytes);
    }

    public void SetUploadDestination(string token, IManagedHostFileUpload destination,
        CancellationToken cancellationToken, string destinationPath)
    {
        var transfer = GetUpload(token);
        transfer.DestinationCancellation = cancellationToken;
        transfer.DestinationPath = destinationPath;
        transfer.Cancellation.CancelAfter(TimeSpan.FromMinutes(10));
        transfer.Destination.TrySetResult(destination);
    }

    public void SetUploadProgress(string token, Action<long> reportProgress) => GetUpload(token).ReportProgress = reportProgress;

    public void FailUpload(string token, Exception exception)
    {
        if (!uploads.TryGetValue(token, out var transfer)) return;
        transfer.Destination.TrySetException(exception);
        transfer.Completion.TrySetException(exception);
    }

    public async Task<BrowserUploadChunkResult> AppendUploadChunkAsync(
        string token,
        long offset,
        Stream content,
        CancellationToken cancellationToken)
    {
        var transfer = GetUpload(token);
        var destination = await transfer.Destination.Task.WaitAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.DestinationCancellation, transfer.Cancellation.Token);
        cancellationToken = linked.Token;
        await transfer.SyncRoot.WaitAsync(cancellationToken);
        try
        {
            if (transfer.State is BrowserTransferState.Completed or BrowserTransferState.Cancelled or BrowserTransferState.Failed)
            {
                throw new InvalidOperationException("This upload is no longer accepting data.");
            }

            if (offset != transfer.BytesTransferred)
            {
                throw new InvalidOperationException($"Upload chunk offset {offset} did not match the expected position {transfer.BytesTransferred}.");
            }

            // Only this fixed buffer is retained. Never stage a browser upload in /tmp.
            var buffer = new byte[128 * 1024];
            long bytesWritten = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (bytesWritten + read > 8L * 1024 * 1024 || transfer.BytesTransferred + bytesWritten + read > transfer.TotalBytes)
                    throw new InvalidOperationException("The upload chunk exceeds its allowed size.");
                await destination.Content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                transfer.Cancellation.CancelAfter(TimeSpan.FromMinutes(10));
                bytesWritten += read;
                if (Environment.TickCount64 - transfer.LastProgressAt >= 250)
                {
                    transfer.LastProgressAt = Environment.TickCount64;
                    transfer.ReportProgress?.Invoke(transfer.BytesTransferred + bytesWritten);
                }
            }
            await destination.Content.FlushAsync(cancellationToken);
            transfer.Cancellation.CancelAfter(TimeSpan.FromMinutes(10));
            transfer.BytesTransferred += bytesWritten;
            transfer.State = BrowserTransferState.Running;

            return new BrowserUploadChunkResult(
                transfer.WorkspaceId,
                transfer.JobId,
                transfer.ItemId,
                transfer.BytesTransferred,
                transfer.TotalBytes);
        }
        catch (Exception exception)
        {
            transfer.State = BrowserTransferState.Failed;
            transfer.Completion.TrySetException(exception);
            throw;
        }
        finally
        {
            transfer.SyncRoot.Release();
        }
    }

    public async Task<BrowserUploadCompletionResult> CompleteUploadAsync(string token, CancellationToken cancellationToken)
    {
        var transfer = GetUpload(token);
        var destination = await transfer.Destination.Task.WaitAsync(cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.DestinationCancellation, transfer.Cancellation.Token);
        cancellationToken = linked.Token;
        await transfer.SyncRoot.WaitAsync(cancellationToken);
        try
        {
            if (transfer.State is BrowserTransferState.Cancelled or BrowserTransferState.Failed)
                throw new InvalidOperationException("This upload is no longer accepting data.");
            if (transfer.BytesTransferred != transfer.TotalBytes)
            {
                throw new InvalidOperationException(
                    $"Upload received {transfer.BytesTransferred} byte(s), but {transfer.TotalBytes} byte(s) were expected.");
            }

            if (transfer.State == BrowserTransferState.Completed)
                return new(transfer.WorkspaceId, transfer.JobId, transfer.ItemId, transfer.TotalBytes, transfer.TotalBytes);
            await destination.CompleteAsync(cancellationToken);

            transfer.State = BrowserTransferState.Completed;
            transfer.Completion.TrySetResult(
                new BrowserUploadReadyFile(
                    transfer.WorkspaceId,
                    transfer.JobId,
                    transfer.ItemId,
                    transfer.FileName,
                    transfer.DestinationPath,
                    transfer.TotalBytes));

            return new BrowserUploadCompletionResult(
                transfer.WorkspaceId,
                transfer.JobId,
                transfer.ItemId,
                transfer.TotalBytes,
                transfer.TotalBytes);
        }
        catch (Exception exception)
        {
            transfer.State = BrowserTransferState.Failed;
            transfer.Completion.TrySetException(exception);
            throw;
        }
        finally
        {
            transfer.SyncRoot.Release();
        }
    }

    public async Task CancelUploadAsync(string token, string? reason, CancellationToken cancellationToken = default)
    {
        if (!uploads.TryGetValue(token, out var transfer))
        {
            return;
        }

        if (transfer.State == BrowserTransferState.Completed) return;
        try { transfer.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { return; } // The queue already released this transfer.
        await transfer.SyncRoot.WaitAsync(cancellationToken);
        try
        {
            if (transfer.State is BrowserTransferState.Completed or BrowserTransferState.Cancelled)
            {
                return;
            }

            transfer.State = BrowserTransferState.Cancelled;
            transfer.Destination.TrySetCanceled();
            transfer.Completion.TrySetCanceled(cancellationToken);
        }
        finally
        {
            transfer.SyncRoot.Release();
        }

    }

    public Task<BrowserUploadReadyFile> WaitForUploadAsync(string token, CancellationToken cancellationToken)
    {
        var transfer = GetUpload(token);
        return transfer.Completion.Task.WaitAsync(transfer.Cancellation.Token).WaitAsync(cancellationToken);
    }

    public void ReleaseUpload(string token)
    {
        if (!uploads.TryRemove(token, out var transfer))
        {
            return;
        }
        transfer.Cancellation.Dispose();
    }

    public BrowserDownloadRegistration RegisterDownload(
        string workspaceId,
        Guid jobId,
        Guid itemId,
        string localFilePath,
        string downloadFileName,
        string contentType,
        long totalBytes)
    {
        var token = Guid.NewGuid().ToString("N");
        var transfer = new BrowserDownloadTransfer(
            token,
            workspaceId,
            jobId,
            itemId,
            localFilePath,
            SanitizeFileName(downloadFileName),
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            totalBytes);

        downloads[token] = transfer;
        return new BrowserDownloadRegistration(token, transfer.DownloadFileName, transfer.ContentType, totalBytes);
    }

    public BrowserDownloadArtifact GetDownloadArtifact(string token)
    {
        if (!TryGetDownload(token, out var transfer))
        {
            throw new InvalidOperationException("The requested download is no longer available.");
        }

        return new BrowserDownloadArtifact(
            transfer.WorkspaceId,
            transfer.JobId,
            transfer.ItemId,
            transfer.LocalFilePath,
            transfer.DownloadFileName,
            transfer.ContentType,
            transfer.TotalBytes,
            transfer.BytesTransferred);
    }

    public bool TryGetDownloadArtifact(
        string token,
        [NotNullWhen(true)] out BrowserDownloadArtifact? artifact)
    {
        artifact = null;
        if (!TryGetDownload(token, out var transfer))
        {
            return false;
        }

        artifact = new BrowserDownloadArtifact(
            transfer.WorkspaceId,
            transfer.JobId,
            transfer.ItemId,
            transfer.LocalFilePath,
            transfer.DownloadFileName,
            transfer.ContentType,
            transfer.TotalBytes,
            transfer.BytesTransferred);
        return true;
    }

    public BrowserDownloadProgressResult ReportDownloadProgress(string token, long bytesTransferred)
    {
        if (!TryReportDownloadProgress(token, bytesTransferred, out var result))
        {
            throw new InvalidOperationException("The requested download is no longer available.");
        }

        return result;
    }

    public bool TryReportDownloadProgress(
        string token,
        long bytesTransferred,
        [NotNullWhen(true)] out BrowserDownloadProgressResult? result)
    {
        result = null;
        if (!TryGetDownload(token, out var transfer) ||
            transfer.State is BrowserTransferState.Completed or BrowserTransferState.Cancelled or BrowserTransferState.Failed)
        {
            return false;
        }

        transfer.BytesTransferred = Math.Clamp(bytesTransferred, 0, transfer.TotalBytes);
        transfer.State = BrowserTransferState.Running;

        result = new BrowserDownloadProgressResult(
            transfer.WorkspaceId,
            transfer.JobId,
            transfer.ItemId,
            transfer.BytesTransferred,
            transfer.TotalBytes);
        return true;
    }

    public Task<BrowserDownloadReadyFile> WaitForDownloadCompletionAsync(string token, CancellationToken cancellationToken)
    {
        var transfer = GetDownload(token);
        cancellationToken.Register(() => transfer.Completion.TrySetCanceled(cancellationToken));
        return transfer.Completion.Task;
    }

    public async Task<BrowserDownloadCompletionResult> StreamDownloadToAsync(
        string token,
        Stream destination,
        IProgress<BrowserDownloadProgressResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var transfer = GetDownload(token);
        var buffer = new byte[1024 * 128];
        var stopwatch = Stopwatch.StartNew();
        long totalBytesTransferred = 0;
        long lastReportedBytes = -1;
        var lastReportedAt = TimeSpan.Zero;

        try
        {
            transfer.State = BrowserTransferState.Running;

            await using var sourceStream = new FileStream(
                transfer.LocalFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                useAsync: true);

            while (true)
            {
                var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalBytesTransferred += bytesRead;
                ReportDownloadProgress(
                    transfer,
                    totalBytesTransferred,
                    progress,
                    force: false,
                    stopwatch.Elapsed,
                    ref lastReportedBytes,
                    ref lastReportedAt);
            }

            await destination.FlushAsync(cancellationToken);

            if (totalBytesTransferred != transfer.TotalBytes)
            {
                throw new IOException(
                    $"Browser download streamed {totalBytesTransferred} byte(s), but {transfer.TotalBytes} byte(s) were expected.");
            }

            ReportDownloadProgress(
                transfer,
                totalBytesTransferred,
                progress,
                force: true,
                stopwatch.Elapsed,
                ref lastReportedBytes,
                ref lastReportedAt);

            return CompleteDownloadTransfer(transfer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            transfer.State = BrowserTransferState.Cancelled;
            transfer.Completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            transfer.State = BrowserTransferState.Failed;
            transfer.Completion.TrySetException(exception);
            throw;
        }
    }

    public Task<BrowserDownloadCompletionResult> CompleteDownloadAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!TryCompleteDownload(token, out var result))
        {
            throw new InvalidOperationException("The requested download is no longer available.");
        }

        return Task.FromResult(result);
    }

    public bool TryCompleteDownload(
        string token,
        [NotNullWhen(true)] out BrowserDownloadCompletionResult? result)
    {
        result = null;
        if (!TryGetDownload(token, out var transfer) ||
            transfer.State is BrowserTransferState.Cancelled or BrowserTransferState.Failed)
        {
            return false;
        }

        result = CompleteDownloadTransfer(transfer);
        return true;
    }

    public Task FailDownloadAsync(string token, string? reason, CancellationToken cancellationToken = default)
    {
        if (!downloads.TryGetValue(token, out var transfer))
        {
            return Task.CompletedTask;
        }

        transfer.State = BrowserTransferState.Failed;
        transfer.Completion.TrySetException(new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? "Browser download failed." : reason));
        return Task.CompletedTask;
    }

    public void ReleaseDownload(string token)
    {
        if (!downloads.TryRemove(token, out var transfer))
        {
            return;
        }

        TryDeleteFile(transfer.LocalFilePath);
    }

    private BrowserUploadTransfer GetUpload(string token)
    {
        if (!uploads.TryGetValue(token, out var transfer))
        {
            throw new InvalidOperationException("The requested upload is no longer available.");
        }

        return transfer;
    }

    private BrowserDownloadTransfer GetDownload(string token)
    {
        if (!TryGetDownload(token, out var transfer))
        {
            throw new InvalidOperationException("The requested download is no longer available.");
        }

        return transfer;
    }

    private bool TryGetDownload(
        string token,
        [NotNullWhen(true)] out BrowserDownloadTransfer? transfer) =>
        downloads.TryGetValue(token, out transfer);

    private static BrowserDownloadCompletionResult CompleteDownloadTransfer(BrowserDownloadTransfer transfer)
    {
        transfer.BytesTransferred = transfer.TotalBytes;
        transfer.State = BrowserTransferState.Completed;
        transfer.Completion.TrySetResult(
            new BrowserDownloadReadyFile(
                transfer.WorkspaceId,
                transfer.JobId,
                transfer.ItemId,
                transfer.DownloadFileName,
                transfer.LocalFilePath,
                transfer.TotalBytes));

        return new BrowserDownloadCompletionResult(
            transfer.WorkspaceId,
            transfer.JobId,
            transfer.ItemId,
            transfer.BytesTransferred,
            transfer.TotalBytes);
    }

    private static void ReportDownloadProgress(
        BrowserDownloadTransfer transfer,
        long bytesTransferred,
        IProgress<BrowserDownloadProgressResult>? progress,
        bool force,
        TimeSpan elapsed,
        ref long lastReportedBytes,
        ref TimeSpan lastReportedAt)
    {
        transfer.BytesTransferred = Math.Clamp(bytesTransferred, 0, transfer.TotalBytes);

        if (!force &&
            transfer.BytesTransferred < transfer.TotalBytes &&
            transfer.BytesTransferred - lastReportedBytes < 1024 * 1024 &&
            elapsed - lastReportedAt < TimeSpan.FromMilliseconds(180))
        {
            return;
        }

        lastReportedBytes = transfer.BytesTransferred;
        lastReportedAt = elapsed;
        progress?.Report(
            new BrowserDownloadProgressResult(
                transfer.WorkspaceId,
                transfer.JobId,
                transfer.ItemId,
                transfer.BytesTransferred,
                transfer.TotalBytes));
    }

    private static string SanitizeFileName(string fileName)
    {
        fileName = fileName.Trim().Replace('\\', '/');
        var lastSeparator = fileName.LastIndexOf('/');
        if (lastSeparator >= 0)
        {
            fileName = fileName[(lastSeparator + 1)..];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Create(fileName.Length, (fileName, invalid), static (span, state) =>
        {
            for (var index = 0; index < state.fileName.Length; index++)
            {
                var character = state.fileName[index];
                span[index] = Array.IndexOf(state.invalid, character) >= 0 ? '_' : character;
            }
        }).Trim();

        return string.IsNullOrWhiteSpace(sanitized)
            ? "transfer.bin"
            : sanitized;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private enum BrowserTransferState
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Cancelled = 3,
        Failed = 4
    }

    private sealed class BrowserUploadTransfer(
        string token,
        string workspaceId,
        Guid jobId,
        Guid itemId,
        string fileName,
        long totalBytes)
    {
        public string Token { get; } = token;
        public string WorkspaceId { get; } = workspaceId;
        public Guid JobId { get; } = jobId;
        public Guid ItemId { get; } = itemId;
        public string FileName { get; } = fileName;
        public TaskCompletionSource<IManagedHostFileUpload> Destination { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DestinationPath { get; set; } = string.Empty;
        public Action<long>? ReportProgress { get; set; }
        public long LastProgressAt { get; set; }
        public CancellationToken DestinationCancellation { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public long TotalBytes { get; } = totalBytes;
        public long BytesTransferred { get; set; }
        public BrowserTransferState State { get; set; } = BrowserTransferState.Pending;
        public SemaphoreSlim SyncRoot { get; } = new(1, 1);
        public TaskCompletionSource<BrowserUploadReadyFile> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BrowserDownloadTransfer(
        string token,
        string workspaceId,
        Guid jobId,
        Guid itemId,
        string localFilePath,
        string downloadFileName,
        string contentType,
        long totalBytes)
    {
        public string Token { get; } = token;
        public string WorkspaceId { get; } = workspaceId;
        public Guid JobId { get; } = jobId;
        public Guid ItemId { get; } = itemId;
        public string LocalFilePath { get; } = localFilePath;
        public string DownloadFileName { get; } = downloadFileName;
        public string ContentType { get; } = contentType;
        public long TotalBytes { get; } = totalBytes;
        public long BytesTransferred { get; set; }
        public BrowserTransferState State { get; set; } = BrowserTransferState.Pending;
        public TaskCompletionSource<BrowserDownloadReadyFile> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record BrowserUploadRegistration(
    string Token,
    string FileName,
    long TotalBytes);

public sealed record BrowserUploadChunkResult(
    string WorkspaceId,
    Guid JobId,
    Guid ItemId,
    long BytesTransferred,
    long TotalBytes);

public sealed record BrowserUploadCompletionResult(
    string WorkspaceId,
    Guid JobId,
    Guid ItemId,
    long BytesTransferred,
    long TotalBytes);

public sealed record BrowserUploadReadyFile(
    string WorkspaceId,
    Guid JobId,
    Guid ItemId,
    string FileName,
    string DestinationPath,
    long TotalBytes);

public sealed record BrowserDownloadRegistration(
    string Token,
    string DownloadFileName,
    string ContentType,
    long TotalBytes);

public sealed record BrowserDownloadArtifact(
    string WorkspaceId,
    Guid JobId,
    Guid ItemId,
    string LocalFilePath,
    string DownloadFileName,
    string ContentType,
    long TotalBytes,
    long BytesTransferred);

public sealed record BrowserDownloadProgressResult(
    string WorkspaceId,
    Guid JobId,
    Guid ItemId,
    long BytesTransferred,
    long TotalBytes);

public sealed record BrowserDownloadReadyFile(
    string WorkspaceId,
    Guid JobId,
    Guid ItemId,
    string FileName,
    string LocalFilePath,
    long TotalBytes);

public sealed record BrowserDownloadCompletionResult(
    string WorkspaceId,
    Guid JobId,
    Guid ItemId,
    long BytesTransferred,
    long TotalBytes);

public sealed record BrowserDownloadProgressUpdate(long BytesTransferred);

public sealed record BrowserTransferCancelRequest(string? Reason);
