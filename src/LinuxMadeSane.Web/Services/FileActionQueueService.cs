// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Collections.Concurrent;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxMadeSane.Web.Services;

// Guardrail: queued file actions must stay on the shared host file-access boundary.
// Do not add direct local/SFTP branching or raw credential plumbing here; that logic
// belongs in IManagedHostFileAccessService and the transient secret store.
public sealed class FileActionQueueService(
    IServiceScopeFactory scopeFactory,
    FileBrowserWorkspaceRegistry workspaceRegistry,
    BrowserFileTransferService browserFileTransferService,
    ITransientConnectionSecretStore transientConnectionSecretStore)
{
    private readonly ConcurrentDictionary<string, FileActionSessionState> sessions = new(StringComparer.Ordinal);

    public event Action<FileActionQueueChangedEvent>? Changed;

    public IReadOnlyList<FileActionJobSnapshot> GetJobs(string workspaceId)
    {
        var session = GetSession(workspaceId);
        lock (session.SyncRoot)
        {
            return session.Jobs.Values
                .Select(CreateSnapshot)
                .OrderByDescending(GetPriorityRank)
                .ThenByDescending(job => job.CreatedAtUtc)
                .ToArray();
        }
    }

    public Guid QueuePaste(string workspaceId, FileActionPasteRequest request)
    {
        var session = GetSession(workspaceId);
        FileActionJob job;

        lock (session.SyncRoot)
        {
            job = new FileActionJob(
                Guid.NewGuid(),
                request.Kind,
                request.SourceHost,
                CaptureExecutionContext(request.SourceExecution),
                request.DestinationHost,
                CaptureExecutionContext(request.DestinationExecution),
                request.DestinationDirectoryPath,
                request.Items
                    .Select(item => new FileActionJobItem(
                        Guid.NewGuid(),
                        item.DisplayName,
                        NormalizeBrowserPath(item.SourcePath),
                        CombineBrowserPath(request.DestinationDirectoryPath, item.DisplayName),
                        item.IsDirectory))
                    .ToList(),
                DateTimeOffset.UtcNow);

            session.Jobs[job.Id] = job;
        }

        Notify(workspaceId, job);
        _ = Task.Run(() => ExecuteJobAsync(workspaceId, job.Id));
        return job.Id;
    }

    public Guid QueueDelete(string workspaceId, FileActionDeleteRequest request)
    {
        var session = GetSession(workspaceId);
        FileActionJob job;

        lock (session.SyncRoot)
        {
            job = new FileActionJob(
                Guid.NewGuid(),
                FileActionKind.Delete,
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.ActionDirectoryPath,
                request.Items
                    .Select(item => new FileActionJobItem(
                        Guid.NewGuid(),
                        item.DisplayName,
                        NormalizeBrowserPath(item.SourcePath),
                        null,
                        item.IsDirectory))
                    .ToList(),
                DateTimeOffset.UtcNow);

            session.Jobs[job.Id] = job;
        }

        Notify(workspaceId, job);
        _ = Task.Run(() => ExecuteJobAsync(workspaceId, job.Id));
        return job.Id;
    }

    public Guid QueueCreateFolder(string workspaceId, FileActionCreateFolderRequest request)
    {
        var session = GetSession(workspaceId);
        FileActionJob job;

        lock (session.SyncRoot)
        {
            job = new FileActionJob(
                Guid.NewGuid(),
                FileActionKind.CreateFolder,
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.ParentDirectoryPath,
                [
                    new FileActionJobItem(
                        Guid.NewGuid(),
                        request.DisplayName,
                        request.DestinationPath,
                        request.DestinationPath,
                        true)
                ],
                DateTimeOffset.UtcNow);

            session.Jobs[job.Id] = job;
        }

        Notify(workspaceId, job);
        _ = Task.Run(() => ExecuteJobAsync(workspaceId, job.Id));
        return job.Id;
    }

    public Guid QueueZip(string workspaceId, FileActionZipRequest request)
    {
        var session = GetSession(workspaceId);
        FileActionJob job;

        lock (session.SyncRoot)
        {
            job = new FileActionJob(
                Guid.NewGuid(),
                FileActionKind.Zip,
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.ActionDirectoryPath,
                [
                    new FileActionJobItem(
                        Guid.NewGuid(),
                        request.DisplayName,
                        request.SourcePath,
                        request.DestinationPath,
                        request.IsDirectory)
                ],
                DateTimeOffset.UtcNow,
                request.Format);

            session.Jobs[job.Id] = job;
        }

        Notify(workspaceId, job);
        _ = Task.Run(() => ExecuteJobAsync(workspaceId, job.Id));
        return job.Id;
    }

    public Guid QueueExtract(string workspaceId, FileActionExtractRequest request)
    {
        var session = GetSession(workspaceId);
        FileActionJob job;

        lock (session.SyncRoot)
        {
            job = new FileActionJob(
                Guid.NewGuid(),
                FileActionKind.Extract,
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.ActionDirectoryPath,
                [
                    new FileActionJobItem(
                        Guid.NewGuid(),
                        request.DisplayName,
                        request.SourceArchivePath,
                        request.DestinationDirectoryPath,
                        true)
                ],
                DateTimeOffset.UtcNow,
                request.Format);

            session.Jobs[job.Id] = job;
        }

        Notify(workspaceId, job);
        _ = Task.Run(() => ExecuteJobAsync(workspaceId, job.Id));
        return job.Id;
    }

    public FileActionUploadLaunchPlan QueueUpload(string workspaceId, FileActionUploadRequest request)
    {
        var session = GetSession(workspaceId);
        FileActionJob job;
        FileActionUploadItemLaunchPlan[] launchItems;

        lock (session.SyncRoot)
        {
            var createdAtUtc = DateTimeOffset.UtcNow;
            var jobId = Guid.NewGuid();
            var offsetBytes = 0L;
            var items = new List<FileActionJobItem>(request.Items.Count);
            var launchPlans = new List<FileActionUploadItemLaunchPlan>(request.Items.Count);

            foreach (var sourceItem in request.Items)
            {
                var itemId = Guid.NewGuid();
                var destinationPath = CombineBrowserPath(request.DestinationDirectoryPath, sourceItem.FileName);
                var uploadRegistration = browserFileTransferService.RegisterUpload(
                    workspaceId,
                    jobId,
                    itemId,
                    sourceItem.FileName,
                    sourceItem.SizeBytes);

                var item = new FileActionJobItem(
                    itemId,
                    sourceItem.FileName,
                    sourceItem.FileName,
                    destinationPath,
                    false)
                {
                    BrowserTransferToken = uploadRegistration.Token,
                    ExpectedBytes = sourceItem.SizeBytes,
                    TransferOffsetBytes = offsetBytes,
                    TransferTotalBytes = CalculateItemTransferTotalBytes(FileActionKind.Upload, sourceItem.SizeBytes),
                    TransferPhase = "Uploading to Linux Made Sane"
                };

                items.Add(item);
                launchPlans.Add(new FileActionUploadItemLaunchPlan(itemId, sourceItem.ClientFileId, sourceItem.FileName, uploadRegistration.Token, sourceItem.SizeBytes));
                offsetBytes += item.TransferTotalBytes ?? 0;
            }

            job = new FileActionJob(
                jobId,
                FileActionKind.Upload,
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.DestinationDirectoryPath,
                items,
                createdAtUtc)
            {
                TotalBytes = offsetBytes
            };

            session.Jobs[job.Id] = job;
            launchItems = launchPlans.ToArray();
        }

        Notify(workspaceId, job);
        _ = Task.Run(() => ExecuteJobAsync(workspaceId, job.Id));
        return new FileActionUploadLaunchPlan(job.Id, launchItems);
    }

    public Guid QueueDownload(string workspaceId, FileActionDownloadRequest request)
    {
        var session = GetSession(workspaceId);
        FileActionJob job;

        lock (session.SyncRoot)
        {
            var item = new FileActionJobItem(
                Guid.NewGuid(),
                request.DisplayName,
                request.SourcePath,
                request.DownloadFileName,
                request.IsDirectory)
            {
                ExpectedBytes = request.EstimatedSizeBytes,
                TransferOffsetBytes = 0,
                TransferTotalBytes = request.EstimatedSizeBytes.HasValue
                    ? CalculateItemTransferTotalBytes(FileActionKind.Download, request.EstimatedSizeBytes.Value)
                    : null,
                TransferPhase = request.IsDirectory ? "Preparing archive" : "Downloading to Linux Made Sane"
            };

            job = new FileActionJob(
                Guid.NewGuid(),
                FileActionKind.Download,
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.Host,
                CaptureExecutionContext(request.Execution),
                request.ActionDirectoryPath,
                [item],
                DateTimeOffset.UtcNow)
            {
                TotalBytes = item.TransferTotalBytes
            };

            session.Jobs[job.Id] = job;
        }

        Notify(workspaceId, job);
        _ = Task.Run(() => ExecuteJobAsync(workspaceId, job.Id));
        return job.Id;
    }

    public void ReportBrowserUploadProgress(string workspaceId, Guid jobId, Guid itemId, long bytesTransferred, long totalBytes)
    {
        if (!TryUpdateTransferProgress(
                workspaceId,
                jobId,
                itemId,
                bytesTransferred,
                totalBytes,
                "Uploading to Linux Made Sane"))
        {
            return;
        }
    }

    public void ReportBrowserDownloadProgress(string workspaceId, Guid jobId, Guid itemId, long bytesTransferred, long totalBytes)
    {
        if (!TryUpdateTransferProgress(
                workspaceId,
                jobId,
                itemId,
                bytesTransferred,
                totalBytes,
                "Saving to your browser"))
        {
            return;
        }
    }

    public FileActionBrowserDownloadLaunchRequest? TryBeginBrowserDownload(string workspaceId, Guid jobId)
    {
        var session = GetSession(workspaceId);
        FileActionJob? job;
        FileActionBrowserDownloadLaunchRequest? launchRequest;

        lock (session.SyncRoot)
        {
            if (!session.Jobs.TryGetValue(jobId, out job) ||
                job.Kind != FileActionKind.Download ||
                string.IsNullOrWhiteSpace(job.BrowserDownloadToken) ||
                string.IsNullOrWhiteSpace(job.BrowserDownloadFileName) ||
                job.BrowserDownloadStarted)
            {
                return null;
            }

            job.BrowserDownloadStarted = true;
            launchRequest = new FileActionBrowserDownloadLaunchRequest(
                job.Id,
                job.BrowserDownloadToken,
                job.BrowserDownloadFileName,
                job.BrowserDownloadContentType ?? "application/octet-stream",
                job.BrowserDownloadTotalBytes ?? 0);
        }

        Notify(workspaceId, job!);
        return launchRequest;
    }

    public Task RetryAsync(string workspaceId, Guid jobId)
    {
        var session = GetSession(workspaceId);
        FileActionJob? job;

        lock (session.SyncRoot)
        {
            if (!session.Jobs.TryGetValue(jobId, out job) ||
                job.Status != FileActionJobStatus.NeedsAttention ||
                job.PendingDecision is null)
            {
                return Task.CompletedTask;
            }

            var failedItem = job.Items.FirstOrDefault(item => item.Id == job.FailedItemId);
            if (failedItem is not null)
            {
                failedItem.Status = FileActionItemStatus.Pending;
                failedItem.Message = string.Empty;
                failedItem.CompletedAtUtc = null;
            }

            job.Status = FileActionJobStatus.Running;
            job.StatusMessage = "Retrying the failed item.";
            job.FailedItemId = null;
            var decision = job.PendingDecision;
            job.PendingDecision = null;
            decision.TrySetResult(FileActionFailureDecision.Retry);
        }

        Notify(workspaceId, job!);
        return Task.CompletedTask;
    }

    public Task IgnoreAndContinueAsync(string workspaceId, Guid jobId)
    {
        var session = GetSession(workspaceId);
        FileActionJob? job;

        lock (session.SyncRoot)
        {
            if (!session.Jobs.TryGetValue(jobId, out job) ||
                job.Status != FileActionJobStatus.NeedsAttention ||
                job.PendingDecision is null)
            {
                return Task.CompletedTask;
            }

            var failedItem = job.Items.FirstOrDefault(item => item.Id == job.FailedItemId);
            if (failedItem is not null)
            {
                failedItem.Status = FileActionItemStatus.Ignored;
                failedItem.CompletedAtUtc = DateTimeOffset.UtcNow;
            }

            job.HasIgnoredItems = true;
            job.Status = FileActionJobStatus.Running;
            job.StatusMessage = "Ignoring the failed item and continuing.";
            job.FailedItemId = null;
            var decision = job.PendingDecision;
            job.PendingDecision = null;
            decision.TrySetResult(FileActionFailureDecision.IgnoreAndContinue);
        }

        Notify(workspaceId, job!);
        return Task.CompletedTask;
    }

    public Task CancelAsync(string workspaceId, Guid jobId)
    {
        var session = GetSession(workspaceId);
        FileActionJob? job;

        lock (session.SyncRoot)
        {
            if (!session.Jobs.TryGetValue(jobId, out job) || IsTerminal(job.Status))
            {
                return Task.CompletedTask;
            }

            job.Cancellation.Cancel();
            job.StatusMessage = "Cancelling file action...";

            if (job.PendingDecision is not null)
            {
                var decision = job.PendingDecision;
                job.PendingDecision = null;
                decision.TrySetResult(FileActionFailureDecision.Cancel);
            }
        }

        Notify(workspaceId, job!);
        return Task.CompletedTask;
    }

    public Task DismissAsync(string workspaceId, Guid jobId)
    {
        var session = GetSession(workspaceId);
        var removed = false;

        lock (session.SyncRoot)
        {
            if (session.Jobs.TryGetValue(jobId, out var job) && IsTerminal(job.Status))
            {
                removed = session.Jobs.Remove(jobId);
            }
        }

        if (removed)
        {
            Changed?.Invoke(new FileActionQueueChangedEvent(workspaceId, jobId, FileActionJobStatus.Dismissed, Array.Empty<FileActionAffectedLocation>()));
        }

        return Task.CompletedTask;
    }

    public Task ClearCompletedAsync(string workspaceId)
    {
        var session = GetSession(workspaceId);
        var removed = false;

        lock (session.SyncRoot)
        {
            foreach (var jobId in session.Jobs.Values
                         .Where(job => IsTerminal(job.Status))
                         .Select(job => job.Id)
                         .ToArray())
            {
                removed |= session.Jobs.Remove(jobId);
            }
        }

        if (removed)
        {
            Changed?.Invoke(new FileActionQueueChangedEvent(workspaceId, Guid.Empty, FileActionJobStatus.Dismissed, Array.Empty<FileActionAffectedLocation>()));
        }

        return Task.CompletedTask;
    }

    private async Task ExecuteJobAsync(string workspaceId, Guid jobId)
    {
        var session = GetSession(workspaceId);
        FileActionJob? job;

        lock (session.SyncRoot)
        {
            if (!session.Jobs.TryGetValue(jobId, out job))
            {
                return;
            }

            job.Status = FileActionJobStatus.Running;
            job.StartedAtUtc ??= DateTimeOffset.UtcNow;
            job.StatusMessage = $"Starting {GetKindLabel(job.Kind).ToLowerInvariant()} action.";
        }

        Notify(workspaceId, job!);

        try
        {
            while (true)
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();

                FileActionJobItem? nextItem;
                lock (session.SyncRoot)
                {
                    nextItem = job.Items.FirstOrDefault(item => item.Status == FileActionItemStatus.Pending);
                    if (nextItem is not null)
                    {
                        nextItem.Status = FileActionItemStatus.Running;
                        nextItem.StartedAtUtc = DateTimeOffset.UtcNow;
                        nextItem.Message = string.Empty;
                        job.ActiveItemId = nextItem.Id;
                        job.Status = FileActionJobStatus.Running;
                        job.StatusMessage = BuildRunningMessage(job.Kind, nextItem.DisplayName);
                    }
                }

                if (nextItem is null)
                {
                    break;
                }

                Notify(workspaceId, job);

                try
                {
                    nextItem.ResultPath = await ExecuteItemAsync(
                        workspaceId,
                        job,
                        nextItem,
                        job.Cancellation.Token,
                        detail =>
                        {
                            lock (session.SyncRoot)
                            {
                                job.CurrentDetail = detail;
                            }

                            Notify(workspaceId, job);
                        });

                    lock (session.SyncRoot)
                    {
                        nextItem.Status = FileActionItemStatus.Succeeded;
                        nextItem.CompletedAtUtc = DateTimeOffset.UtcNow;
                        nextItem.Message = BuildSuccessMessage(job.Kind, nextItem);
                        job.ActiveItemId = null;
                        job.CurrentDetail = null;
                    }

                    Notify(workspaceId, job);
                }
                catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
                {
                    lock (session.SyncRoot)
                    {
                        nextItem.Status = FileActionItemStatus.Cancelled;
                        nextItem.CompletedAtUtc = DateTimeOffset.UtcNow;
                        nextItem.Message = "Cancelled.";
                    }

                    throw;
                }
                catch (Exception exception)
                {
                    var failureMessage = exception.Message;

                    if (job.Kind == FileActionKind.Delete)
                    {
                        lock (session.SyncRoot)
                        {
                            nextItem.Status = FileActionItemStatus.Ignored;
                            nextItem.CompletedAtUtc = DateTimeOffset.UtcNow;
                            nextItem.Message = $"Skipped: {failureMessage}";
                            job.HasIgnoredItems = true;
                            job.Status = FileActionJobStatus.Running;
                            job.StatusMessage = "Delete skipped an item and continued.";
                            job.FailedItemId = null;
                            job.ActiveItemId = null;
                            job.CurrentDetail = null;
                        }

                        Notify(workspaceId, job);
                        continue;
                    }

                    Task<FileActionFailureDecision> decisionTask;

                    lock (session.SyncRoot)
                    {
                        nextItem.Status = FileActionItemStatus.Failed;
                        nextItem.CompletedAtUtc = DateTimeOffset.UtcNow;
                        nextItem.Message = failureMessage;
                        job.Status = FileActionJobStatus.NeedsAttention;
                        job.StatusMessage = "A file action needs your attention.";
                        job.FailedItemId = nextItem.Id;
                        job.ActiveItemId = null;
                        job.CurrentDetail = null;
                        job.PendingDecision = new TaskCompletionSource<FileActionFailureDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                        decisionTask = job.PendingDecision.Task;
                    }

                    Notify(workspaceId, job);
                    await decisionTask;
                }
            }

            lock (session.SyncRoot)
            {
                job.Status = job.HasIgnoredItems
                    ? FileActionJobStatus.CompletedWithIssues
                    : FileActionJobStatus.Succeeded;
                job.StatusMessage = job.HasIgnoredItems
                    ? "Completed with ignored items."
                    : "Completed successfully.";
                job.CompletedAtUtc = DateTimeOffset.UtcNow;
                job.ActiveItemId = null;
                job.FailedItemId = null;
                job.CurrentDetail = null;
            }

            if (job.Kind == FileActionKind.Move && !job.HasIgnoredItems)
            {
                workspaceRegistry.GetOrCreate(workspaceId).ClearClipboard();
            }

            Notify(workspaceId, job);
        }
        catch (OperationCanceledException)
        {
            lock (session.SyncRoot)
            {
                job.Status = FileActionJobStatus.Cancelled;
                job.StatusMessage = "Cancelled.";
                job.CompletedAtUtc = DateTimeOffset.UtcNow;
                job.ActiveItemId = null;
                job.FailedItemId = null;

                foreach (var item in job.Items.Where(item => item.Status == FileActionItemStatus.Pending || item.Status == FileActionItemStatus.Running))
                {
                    item.Status = FileActionItemStatus.Cancelled;
                    item.CompletedAtUtc = DateTimeOffset.UtcNow;
                    item.Message = "Cancelled.";
                }

                job.CurrentDetail = null;
            }

            Notify(workspaceId, job!);
        }
        catch (Exception exception)
        {
            lock (session.SyncRoot)
            {
                job.Status = FileActionJobStatus.Failed;
                job.StatusMessage = exception.Message;
                job.CompletedAtUtc = DateTimeOffset.UtcNow;
                job.ActiveItemId = null;
                job.CurrentDetail = null;
            }

            Notify(workspaceId, job!);
        }
    }

    private async Task<string?> ExecuteItemAsync(
        string workspaceId,
        FileActionJob job,
        FileActionJobItem item,
        CancellationToken cancellationToken,
        Action<string>? reportCurrentDetail)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var fileAccessService = scope.ServiceProvider.GetRequiredService<IManagedHostFileAccessService>();
        var sameHost = job.SourceHost.Id == job.DestinationHost.Id;
        var sourceProfile = BuildConnectionProfile(job.SourceExecution);
        var destinationProfile = BuildConnectionProfile(job.DestinationExecution);

        return job.Kind switch
        {
            FileActionKind.Copy when sameHost => await fileAccessService.CopyAsync(
                job.DestinationHost,
                item.SourcePath,
                item.DestinationPath!,
                destinationProfile,
                cancellationToken),
            FileActionKind.Move when sameHost => await fileAccessService.MoveAsync(
                job.DestinationHost,
                item.SourcePath,
                item.DestinationPath!,
                destinationProfile,
                cancellationToken),
            FileActionKind.Copy or FileActionKind.Move => await ExecuteCrossHostTransferAsync(
                job,
                item,
                fileAccessService,
                sourceProfile,
                destinationProfile,
                cancellationToken,
                reportCurrentDetail),
            FileActionKind.Delete => await ExecuteDeleteAsync(
                job,
                item,
                fileAccessService,
                destinationProfile,
                cancellationToken),
            FileActionKind.CreateFolder => await fileAccessService.CreateDirectoryAsync(
                job.DestinationHost,
                item.SourcePath,
                destinationProfile,
                cancellationToken),
            FileActionKind.Zip => await fileAccessService.CreateArchiveAsync(
                job.DestinationHost,
                item.SourcePath,
                item.DestinationPath!,
                job.ArchiveFormat,
                destinationProfile,
                cancellationToken),
            FileActionKind.Extract => await fileAccessService.ExtractArchiveAsync(
                job.DestinationHost,
                item.SourcePath,
                item.DestinationPath!,
                job.ArchiveFormat,
                destinationProfile,
                cancellationToken),
            FileActionKind.Upload => await ExecuteBrowserUploadAsync(
                workspaceId,
                job,
                item,
                fileAccessService,
                destinationProfile,
                cancellationToken),
            FileActionKind.Download => await ExecuteBrowserDownloadAsync(
                workspaceId,
                job,
                item,
                fileAccessService,
                sourceProfile,
                cancellationToken),
            _ => null
        };
    }

    private static async Task<string?> ExecuteDeleteAsync(
        FileActionJob job,
        FileActionJobItem item,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile destinationProfile,
        CancellationToken cancellationToken)
    {
        await fileAccessService.DeleteAsync(
            job.DestinationHost,
            item.SourcePath,
            destinationProfile,
            item.IsDirectory,
            cancellationToken);

        return null;
    }

    private static async Task<string?> ExecuteCrossHostTransferAsync(
        FileActionJob job,
        FileActionJobItem item,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile sourceProfile,
        ManagedHostConnectionProfile destinationProfile,
        CancellationToken cancellationToken,
        Action<string>? reportCurrentDetail)
    {
        var tempRoot = LmsTemporaryPaths.Combine(
            "file-actions",
            job.Id.ToString("N"),
            item.Id.ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            if (item.IsDirectory)
            {
                await CopyDirectoryAcrossHostsAsync(
                    job,
                    item.SourcePath,
                    item.DestinationPath!,
                    tempRoot,
                    fileAccessService,
                    sourceProfile,
                    destinationProfile,
                    cancellationToken,
                    reportCurrentDetail);
            }
            else
            {
                reportCurrentDetail?.Invoke(item.DisplayName);
                var localFilePath = Path.Combine(tempRoot, SanitizeFileName(item.DisplayName));
                await DownloadSourceFileAsync(
                    job,
                    item.SourcePath,
                    localFilePath,
                    fileAccessService,
                    sourceProfile,
                    cancellationToken);

                await UploadDestinationFileAsync(
                    job,
                    localFilePath,
                    item.DestinationPath!,
                    fileAccessService,
                    destinationProfile,
                    cancellationToken);
            }

            if (job.Kind == FileActionKind.Move)
            {
                await DeleteSourcePathAsync(
                    job,
                    item.SourcePath,
                    item.IsDirectory,
                    fileAccessService,
                    sourceProfile,
                    cancellationToken);
            }

            return item.DestinationPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }
    }

    private static async Task CopyDirectoryAcrossHostsAsync(
        FileActionJob job,
        string sourceRootPath,
        string destinationRootPath,
        string tempRoot,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile sourceProfile,
        ManagedHostConnectionProfile destinationProfile,
        CancellationToken cancellationToken,
        Action<string>? reportCurrentDetail)
    {
        var normalizedSourceRootPath = NormalizeBrowserPath(sourceRootPath);
        await EnsureDestinationDirectoryAsync(
            job,
            destinationRootPath,
            fileAccessService,
            destinationProfile,
            cancellationToken);

        var pendingDirectories = new Stack<(string SourcePath, string DestinationPath)>();
        pendingDirectories.Push((sourceRootPath, destinationRootPath));

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentSourcePath, currentDestinationPath) = pendingDirectories.Pop();
            var children = await ListSourceItemsAsync(
                job,
                currentSourcePath,
                fileAccessService,
                sourceProfile,
                cancellationToken);

            foreach (var child in children)
            {
                var childSourcePath = NormalizeBrowserPath(child.FullPath);
                var childDestinationPath = CombineBrowserPath(currentDestinationPath, child.Name);
                reportCurrentDetail?.Invoke(BuildRelativeTransferPath(normalizedSourceRootPath, childSourcePath));

                if (child.ItemType == SftpItemType.Folder)
                {
                    await EnsureDestinationDirectoryAsync(
                        job,
                        childDestinationPath,
                        fileAccessService,
                        destinationProfile,
                        cancellationToken);

                    pendingDirectories.Push((childSourcePath, childDestinationPath));
                    continue;
                }

                var localFilePath = Path.Combine(tempRoot, $"{Guid.NewGuid():N}-{SanitizeFileName(child.Name)}");
                await DownloadSourceFileAsync(
                    job,
                    childSourcePath,
                    localFilePath,
                    fileAccessService,
                    sourceProfile,
                    cancellationToken);

                try
                {
                    await UploadDestinationFileAsync(
                        job,
                        localFilePath,
                        childDestinationPath,
                        fileAccessService,
                        destinationProfile,
                        cancellationToken);
                }
                finally
                {
                    TryDeleteLocalTempFile(localFilePath);
                }
            }
        }
    }

    private static string BuildRelativeTransferPath(string sourceRootPath, string childSourcePath)
    {
        var normalizedRoot = NormalizeBrowserPath(sourceRootPath).TrimEnd('/');
        var normalizedChild = NormalizeBrowserPath(childSourcePath);
        var prefix = normalizedRoot + "/";
        return normalizedChild.StartsWith(prefix, StringComparison.Ordinal)
            ? normalizedChild[prefix.Length..]
            : Path.GetFileName(normalizedChild);
    }

    private static async Task<IReadOnlyList<SftpItem>> ListSourceItemsAsync(
        FileActionJob job,
        string sourcePath,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile sourceProfile,
        CancellationToken cancellationToken)
        => await fileAccessService.ListItemsAsync(
            job.SourceHost,
            sourcePath,
            sourceProfile,
            cancellationToken);

    private static async Task EnsureDestinationDirectoryAsync(
        FileActionJob job,
        string destinationPath,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile destinationProfile,
        CancellationToken cancellationToken)
        => await fileAccessService.CreateDirectoryAsync(
            job.DestinationHost,
            destinationPath,
            destinationProfile,
            cancellationToken);

    private static async Task CreateArchiveOnSourceAsync(
        FileActionJob job,
        string sourcePath,
        string archivePath,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile sourceProfile,
        CancellationToken cancellationToken)
        => await fileAccessService.CreateZipAsync(
            job.SourceHost,
            sourcePath,
            archivePath,
            sourceProfile,
            cancellationToken);

    private static async Task ExtractArchiveOnDestinationAsync(
        FileActionJob job,
        string archivePath,
        string destinationDirectoryPath,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile destinationProfile,
        CancellationToken cancellationToken)
        => await fileAccessService.ExtractZipAsync(
            job.DestinationHost,
            archivePath,
            destinationDirectoryPath,
            destinationProfile,
            cancellationToken);

    private static async Task DownloadSourceFileAsync(
        FileActionJob job,
        string sourcePath,
        string localDestinationPath,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile sourceProfile,
        CancellationToken cancellationToken,
        IProgress<FileTransferProgress>? progress = null)
        => await fileAccessService.DownloadFileAsync(
            job.SourceHost,
            sourcePath,
            localDestinationPath,
            sourceProfile,
            progress,
            cancellationToken);

    private static async Task UploadDestinationFileAsync(
        FileActionJob job,
        string localSourcePath,
        string destinationPath,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile destinationProfile,
        CancellationToken cancellationToken,
        IProgress<FileTransferProgress>? progress = null)
        => await fileAccessService.UploadFileAsync(
            job.DestinationHost,
            localSourcePath,
            destinationPath,
            destinationProfile,
            progress,
            cancellationToken);

    private static async Task DeleteSourcePathAsync(
        FileActionJob job,
        string sourcePath,
        bool recursive,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile sourceProfile,
        CancellationToken cancellationToken)
        => await fileAccessService.DeleteAsync(
            job.SourceHost,
            sourcePath,
            sourceProfile,
            recursive,
            cancellationToken);

    private static void TryDeleteLocalTempFile(string localFilePath)
    {
        try
        {
            if (File.Exists(localFilePath))
            {
                File.Delete(localFilePath);
            }
        }
        catch
        {
            // Ignore temp cleanup failures.
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            builder.Append(Array.IndexOf(invalidCharacters, character) >= 0 ? '_' : character);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "transfer.bin" : sanitized;
    }

    private async Task<string?> ExecuteBrowserUploadAsync(
        string workspaceId,
        FileActionJob job,
        FileActionJobItem item,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile destinationProfile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.BrowserTransferToken) || string.IsNullOrWhiteSpace(item.DestinationPath))
        {
            throw new InvalidOperationException("Upload staging was not prepared correctly.");
        }

        var stagedUpload = await browserFileTransferService.WaitForUploadAsync(item.BrowserTransferToken, cancellationToken);
        try
        {
            TryUpdateTransferProgress(
                workspaceId,
                job.Id,
                item.Id,
                stagedUpload.TotalBytes,
                stagedUpload.TotalBytes,
                $"Sending to {job.DestinationHost.Name}");

            var uploadProgress = new Progress<FileTransferProgress>(progress =>
            {
                var totalBytes = progress.TotalBytes ?? stagedUpload.TotalBytes;
                TryUpdateTransferProgress(
                    workspaceId,
                    job.Id,
                    item.Id,
                    totalBytes + progress.BytesTransferred,
                    totalBytes,
                    $"Sending to {job.DestinationHost.Name}");
            });

            await fileAccessService.UploadFileAsync(
                job.DestinationHost,
                stagedUpload.LocalFilePath,
                item.DestinationPath,
                destinationProfile,
                uploadProgress,
                cancellationToken);

            return item.DestinationPath;
        }
        finally
        {
            browserFileTransferService.ReleaseUpload(item.BrowserTransferToken);
        }
    }

    private async Task<string?> ExecuteBrowserDownloadAsync(
        string workspaceId,
        FileActionJob job,
        FileActionJobItem item,
        IManagedHostFileAccessService fileAccessService,
        ManagedHostConnectionProfile sourceProfile,
        CancellationToken cancellationToken)
    {
        var stagedRoot = LmsTemporaryPaths.Combine("browser-downloads", job.Id.ToString("N"), item.Id.ToString("N"));
        Directory.CreateDirectory(stagedRoot);
        var stagedFilePath = Path.Combine(stagedRoot, SanitizeFileName(item.DestinationPath ?? item.DisplayName));
        string? remoteArchivePath = null;
        var sourcePath = item.SourcePath;
        var downloadFileName = item.DestinationPath ?? item.DisplayName;

        try
        {
            if (item.IsDirectory)
            {
                remoteArchivePath = BuildTemporaryArchivePath(item.DisplayName);
                downloadFileName = EnsureZipFileName(item.DisplayName);
                item.DestinationPath = downloadFileName;
                TryUpdateTransferProgress(workspaceId, job.Id, item.Id, 0, item.ExpectedBytes ?? 0, "Preparing archive");
                await CreateArchiveOnSourceAsync(job, sourcePath, remoteArchivePath, fileAccessService, sourceProfile, cancellationToken);
                sourcePath = remoteArchivePath;
                stagedFilePath = Path.Combine(stagedRoot, SanitizeFileName(downloadFileName));
            }

            var downloadProgress = new Progress<FileTransferProgress>(progress =>
            {
                TryUpdateTransferProgress(
                    workspaceId,
                    job.Id,
                    item.Id,
                    progress.BytesTransferred,
                    progress.TotalBytes ?? item.ExpectedBytes ?? 0,
                    $"Downloading from {job.SourceHost.Name}");
            });

            await DownloadSourceFileAsync(
                job,
                sourcePath,
                stagedFilePath,
                fileAccessService,
                sourceProfile,
                cancellationToken,
                downloadProgress);

            var stagedInfo = new FileInfo(stagedFilePath);
            item.ExpectedBytes = stagedInfo.Length;
            item.TransferTotalBytes = CalculateItemTransferTotalBytes(job.Kind, stagedInfo.Length);
            RecalculateJobTransferBudget(job);
            TryUpdateTransferProgress(workspaceId, job.Id, item.Id, stagedInfo.Length, stagedInfo.Length, "Waiting for browser download");

            var contentType = downloadFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? "application/zip"
                : "application/octet-stream";
            var registration = browserFileTransferService.RegisterDownload(
                workspaceId,
                job.Id,
                item.Id,
                stagedFilePath,
                downloadFileName,
                contentType,
                stagedInfo.Length);

            job.BrowserDownloadToken = registration.Token;
            job.BrowserDownloadFileName = registration.DownloadFileName;
            job.BrowserDownloadContentType = registration.ContentType;
            job.BrowserDownloadTotalBytes = registration.TotalBytes;
            job.BrowserDownloadStarted = false;
            Notify(workspaceId, job);

            await browserFileTransferService.WaitForDownloadCompletionAsync(registration.Token, cancellationToken);
            TryUpdateTransferProgress(workspaceId, job.Id, item.Id, stagedInfo.Length, stagedInfo.Length, "Saved to your browser");
            return downloadFileName;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(job.BrowserDownloadToken))
            {
                browserFileTransferService.ReleaseDownload(job.BrowserDownloadToken);
            }

            if (!string.IsNullOrWhiteSpace(remoteArchivePath))
            {
                try
                {
                    await fileAccessService.DeleteAsync(
                        job.SourceHost,
                        remoteArchivePath,
                        sourceProfile,
                        recursive: false,
                        cancellationToken);
                }
                catch
                {
                    // Ignore temp archive cleanup failures.
                }
            }

            try
            {
                if (Directory.Exists(stagedRoot))
                {
                    Directory.Delete(stagedRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore local temp cleanup failures.
            }
        }
    }

    private bool TryUpdateTransferProgress(
        string workspaceId,
        Guid jobId,
        Guid itemId,
        long absoluteBytesTransferred,
        long totalBytes,
        string phase)
    {
        var session = GetSession(workspaceId);
        FileActionJob? job;

        lock (session.SyncRoot)
        {
            if (!session.Jobs.TryGetValue(jobId, out job))
            {
                return false;
            }

            var item = job.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is null)
            {
                return false;
            }

            if (totalBytes > 0)
            {
                item.ExpectedBytes = totalBytes;
                item.TransferTotalBytes = CalculateItemTransferTotalBytes(job.Kind, totalBytes);
                RecalculateJobTransferBudget(job);
            }

            UpdateTransferProgress(job, item, absoluteBytesTransferred, totalBytes, phase);
        }

        Notify(workspaceId, job!);
        return true;
    }

    private static void UpdateTransferProgress(
        FileActionJob job,
        FileActionJobItem item,
        long absoluteBytesTransferred,
        long? totalBytes,
        string phase,
        string? detail = null)
    {
        var normalizedTotalBytes = totalBytes.HasValue && totalBytes.Value > 0
            ? totalBytes
            : item.ExpectedBytes;
        if (normalizedTotalBytes.HasValue && normalizedTotalBytes.Value > 0)
        {
            item.ExpectedBytes = normalizedTotalBytes;
            item.TransferTotalBytes = CalculateItemTransferTotalBytes(job.Kind, normalizedTotalBytes.Value);
        }

        item.TransferPhase = phase;
        job.TotalBytes ??= CalculateTotalTransferBytes(job.Items);
        job.BytesTransferred = item.TransferOffsetBytes + Math.Clamp(absoluteBytesTransferred, 0, item.TransferTotalBytes ?? absoluteBytesTransferred);
        job.CurrentDetail = detail;
        job.TransferPhase = phase;
    }

    private static long CalculateItemTransferTotalBytes(FileActionKind kind, long fileBytes) =>
        kind == FileActionKind.Upload ? checked(fileBytes * 2) : fileBytes;

    private static void RecalculateJobTransferBudget(FileActionJob job)
    {
        var totalBytes = CalculateTotalTransferBytes(job.Items);
        if (totalBytes > 0)
        {
            job.TotalBytes = totalBytes;
        }
    }

    private static long CalculateTotalTransferBytes(IEnumerable<FileActionJobItem> items) =>
        items.Sum(item => item.TransferTotalBytes ?? 0);

    private static string EnsureZipFileName(string value) =>
        value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? value
            : value + ".zip";

    private static string BuildTemporaryArchivePath(string displayName) =>
        $"/tmp/.lms-download-{Guid.NewGuid():N}-{SanitizeFileName(EnsureZipFileName(displayName))}";

    // Guardrail: background jobs must keep working after the source page clears or edits
    // its inline credentials, so queue-time execution contexts get their own secret handle.
    private FileActionExecutionContext CaptureExecutionContext(FileActionExecutionContext executionContext) =>
        executionContext with
        {
            SecretHandle = transientConnectionSecretStore.Clone(executionContext.SecretHandle)
        };

    private FileActionSessionState GetSession(string workspaceId) =>
        sessions.GetOrAdd(workspaceId, _ => new FileActionSessionState());

    private void Notify(string workspaceId, FileActionJob job) =>
        Changed?.Invoke(new FileActionQueueChangedEvent(
            workspaceId,
            job.Id,
            job.Status,
            BuildAffectedLocations(job)));

    private static FileActionJobSnapshot CreateSnapshot(FileActionJob job)
    {
        var items = job.Items
            .Select(item => new FileActionJobItemSnapshot(
                item.Id,
                item.DisplayName,
                item.SourcePath,
                item.DestinationPath,
                item.IsDirectory,
                item.Status,
                item.Message,
                item.ResultPath))
            .ToArray();

        var completedCount = items.Count(item => item.Status is FileActionItemStatus.Succeeded or FileActionItemStatus.Ignored);
        var failedCount = items.Count(item => item.Status == FileActionItemStatus.Failed);
        var cancelledCount = items.Count(item => item.Status == FileActionItemStatus.Cancelled);
        var currentItem = items.FirstOrDefault(item => item.Id == job.ActiveItemId);
        var failedItem = items.FirstOrDefault(item => item.Id == job.FailedItemId);

        return new FileActionJobSnapshot(
            job.Id,
            job.Kind,
            job.Status,
            job.DestinationHost.Id,
            job.DestinationHost.Name,
            job.TargetPath,
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.CompletedAtUtc,
            job.StatusMessage,
            items,
            completedCount,
            failedCount,
            cancelledCount,
            currentItem?.DisplayName,
            failedItem?.DisplayName,
            job.CurrentDetail,
            currentItem?.SourcePath,
            currentItem?.DestinationPath,
            job.TransferPhase,
            job.BytesTransferred,
            job.TotalBytes,
            job.BrowserDownloadToken,
            job.BrowserDownloadFileName,
            job.BrowserDownloadContentType,
            job.BrowserDownloadTotalBytes,
            job.Kind == FileActionKind.Download &&
            !string.IsNullOrWhiteSpace(job.BrowserDownloadToken) &&
            !job.BrowserDownloadStarted);
    }

    private static int GetPriorityRank(FileActionJobSnapshot snapshot) => snapshot.Status switch
    {
        FileActionJobStatus.NeedsAttention => 0,
        FileActionJobStatus.Running => 1,
        FileActionJobStatus.Queued => 2,
        _ => 3
    };

    private static bool IsTerminal(FileActionJobStatus status) => status is
        FileActionJobStatus.Succeeded or
        FileActionJobStatus.CompletedWithIssues or
        FileActionJobStatus.Cancelled or
        FileActionJobStatus.Failed;

    private static string GetKindLabel(FileActionKind kind) => kind switch
    {
        FileActionKind.Copy => "Copy",
        FileActionKind.Move => "Move",
        FileActionKind.Delete => "Delete",
        FileActionKind.CreateFolder => "Create folder",
        FileActionKind.Zip => "Compress",
        FileActionKind.Extract => "Uncompress",
        FileActionKind.Upload => "Upload",
        FileActionKind.Download => "Download",
        _ => "File action"
    };

    private static string BuildRunningMessage(FileActionKind kind, string displayName) => kind switch
    {
        FileActionKind.Copy => $"Copying {displayName}...",
        FileActionKind.Move => $"Moving {displayName}...",
        FileActionKind.Delete => $"Deleting {displayName}...",
        FileActionKind.CreateFolder => $"Creating {displayName}...",
        FileActionKind.Zip => $"Compressing {displayName}...",
        FileActionKind.Extract => $"Extracting {displayName}...",
        FileActionKind.Upload => $"Uploading {displayName}...",
        FileActionKind.Download => $"Preparing download for {displayName}...",
        _ => $"Processing {displayName}..."
    };

    private static string BuildSuccessMessage(FileActionKind kind, FileActionJobItem item) => kind switch
    {
        FileActionKind.Copy => $"Copied to {item.DestinationPath}",
        FileActionKind.Move => $"Moved to {item.DestinationPath}",
        FileActionKind.Delete => "Deleted.",
        FileActionKind.CreateFolder => $"Created {item.ResultPath}",
        FileActionKind.Zip => $"Created {item.ResultPath}",
        FileActionKind.Extract => $"Extracted to {item.ResultPath}",
        FileActionKind.Upload => $"Uploaded to {item.DestinationPath}",
        FileActionKind.Download => $"Downloaded {item.DisplayName}",
        _ => "Done."
    };

    private static string NormalizeBrowserPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized.TrimStart('/');
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static string CombineBrowserPath(string directoryPath, string childName)
    {
        var normalizedDirectory = NormalizeBrowserPath(directoryPath);
        var normalizedChild = childName.Trim().TrimStart('/');
        return normalizedDirectory == "/"
            ? "/" + normalizedChild
            : $"{normalizedDirectory}/{normalizedChild}";
    }

    private static ManagedHostConnectionProfile BuildConnectionProfile(FileActionExecutionContext executionContext) =>
        new(executionContext.Username, executionContext.SecretHandle, executionContext.PreferStoredCredentials);

    private static IReadOnlyList<FileActionAffectedLocation> BuildAffectedLocations(FileActionJob job)
    {
        var locations = new Dictionary<(Guid HostId, string Path), FileActionAffectedLocation>();

        if (!string.IsNullOrWhiteSpace(job.TargetPath))
        {
            var normalizedTargetPath = NormalizeBrowserPath(job.TargetPath);
            locations[(job.DestinationHost.Id, normalizedTargetPath)] =
                new FileActionAffectedLocation(job.DestinationHost.Id, normalizedTargetPath);
        }

        foreach (var item in job.Items)
        {
            if (job.Kind is FileActionKind.Move or FileActionKind.Delete)
            {
                AddAffectedLocation(locations, job.SourceHost.Id, item.SourcePath);
            }

            if (!string.IsNullOrWhiteSpace(item.DestinationPath))
            {
                AddAffectedLocation(locations, job.DestinationHost.Id, item.DestinationPath);
            }
        }

        return locations.Values.ToArray();
    }

    private static void AddAffectedLocation(
        IDictionary<(Guid HostId, string Path), FileActionAffectedLocation> locations,
        Guid hostId,
        string path)
    {
        var directoryPath = GetParentDirectory(path);
        var key = (hostId, directoryPath);
        if (!locations.ContainsKey(key))
        {
            locations[key] = new FileActionAffectedLocation(hostId, directoryPath);
        }
    }

    private static string GetParentDirectory(string path)
    {
        var normalizedPath = NormalizeBrowserPath(path);
        if (normalizedPath == "/")
        {
            return "/";
        }

        var separatorIndex = normalizedPath.LastIndexOf('/');
        return separatorIndex <= 0 ? "/" : normalizedPath[..separatorIndex];
    }

    private sealed class FileActionSessionState
    {
        public object SyncRoot { get; } = new();
        public Dictionary<Guid, FileActionJob> Jobs { get; } = [];
    }

    private sealed class FileActionJob(
        Guid id,
        FileActionKind kind,
        ManagedHost sourceHost,
        FileActionExecutionContext sourceExecution,
        ManagedHost destinationHost,
        FileActionExecutionContext destinationExecution,
        string targetPath,
        List<FileActionJobItem> items,
        DateTimeOffset createdAtUtc,
        ArchiveFormat archiveFormat = ArchiveFormat.Zip)
    {
        public Guid Id { get; } = id;
        public FileActionKind Kind { get; } = kind;
        public ManagedHost SourceHost { get; } = sourceHost;
        public FileActionExecutionContext SourceExecution { get; } = sourceExecution;
        public ManagedHost DestinationHost { get; } = destinationHost;
        public FileActionExecutionContext DestinationExecution { get; } = destinationExecution;
        public string TargetPath { get; } = targetPath;
        public List<FileActionJobItem> Items { get; } = items;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public ArchiveFormat ArchiveFormat { get; } = archiveFormat;
        public CancellationTokenSource Cancellation { get; } = new();
        public FileActionJobStatus Status { get; set; } = FileActionJobStatus.Queued;
        public string StatusMessage { get; set; } = "Queued.";
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public Guid? ActiveItemId { get; set; }
        public Guid? FailedItemId { get; set; }
        public string? CurrentDetail { get; set; }
        public string? TransferPhase { get; set; }
        public long BytesTransferred { get; set; }
        public long? TotalBytes { get; set; }
        public string? BrowserDownloadToken { get; set; }
        public string? BrowserDownloadFileName { get; set; }
        public string? BrowserDownloadContentType { get; set; }
        public long? BrowserDownloadTotalBytes { get; set; }
        public bool BrowserDownloadStarted { get; set; }
        public TaskCompletionSource<FileActionFailureDecision>? PendingDecision { get; set; }
        public bool HasIgnoredItems { get; set; }
    }

    private sealed class FileActionJobItem(
        Guid id,
        string displayName,
        string sourcePath,
        string? destinationPath,
        bool isDirectory)
    {
        public Guid Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public string SourcePath { get; } = sourcePath;
        public string? DestinationPath { get; set; } = destinationPath;
        public bool IsDirectory { get; } = isDirectory;
        public FileActionItemStatus Status { get; set; } = FileActionItemStatus.Pending;
        public string Message { get; set; } = string.Empty;
        public string? ResultPath { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public string? BrowserTransferToken { get; set; }
        public long? ExpectedBytes { get; set; }
        public long TransferOffsetBytes { get; set; }
        public long? TransferTotalBytes { get; set; }
        public string? TransferPhase { get; set; }
    }
}

public sealed record FileActionExecutionContext(
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials);

public sealed record FileActionSourceItem(
    string SourcePath,
    string DisplayName,
    bool IsDirectory);

public sealed record FileActionPasteRequest(
    FileActionKind Kind,
    ManagedHost SourceHost,
    FileActionExecutionContext SourceExecution,
    ManagedHost DestinationHost,
    string DestinationDirectoryPath,
    IReadOnlyList<FileActionSourceItem> Items,
    FileActionExecutionContext DestinationExecution);

public sealed record FileActionDeleteRequest(
    ManagedHost Host,
    string ActionDirectoryPath,
    IReadOnlyList<FileActionSourceItem> Items,
    FileActionExecutionContext Execution);

public sealed record FileActionCreateFolderRequest(
    ManagedHost Host,
    string ParentDirectoryPath,
    string DestinationPath,
    string DisplayName,
    FileActionExecutionContext Execution);

public sealed record FileActionZipRequest(
    ManagedHost Host,
    string ActionDirectoryPath,
    string SourcePath,
    string DestinationPath,
    string DisplayName,
    bool IsDirectory,
    FileActionExecutionContext Execution)
{
    public ArchiveFormat Format { get; init; } = ArchiveFormat.Zip;
}

public sealed record FileActionExtractRequest(
    ManagedHost Host,
    string ActionDirectoryPath,
    string SourceArchivePath,
    string DestinationDirectoryPath,
    string DisplayName,
    FileActionExecutionContext Execution)
{
    public ArchiveFormat Format { get; init; } = ArchiveFormat.Zip;
}

public sealed record FileActionUploadSourceItem(
    string ClientFileId,
    string FileName,
    long SizeBytes);

public sealed record FileActionUploadRequest(
    ManagedHost Host,
    string DestinationDirectoryPath,
    IReadOnlyList<FileActionUploadSourceItem> Items,
    FileActionExecutionContext Execution);

public sealed record FileActionUploadLaunchPlan(
    Guid JobId,
    IReadOnlyList<FileActionUploadItemLaunchPlan> Items);

public sealed record FileActionUploadItemLaunchPlan(
    Guid ItemId,
    string ClientFileId,
    string FileName,
    string UploadToken,
    long SizeBytes);

public sealed record FileActionDownloadRequest(
    ManagedHost Host,
    string ActionDirectoryPath,
    string SourcePath,
    string DisplayName,
    bool IsDirectory,
    long? EstimatedSizeBytes,
    string DownloadFileName,
    FileActionExecutionContext Execution);

public sealed record FileActionBrowserDownloadLaunchRequest(
    Guid JobId,
    string DownloadToken,
    string FileName,
    string ContentType,
    long TotalBytes);

public sealed record FileActionQueueChangedEvent(
    string WorkspaceId,
    Guid JobId,
    FileActionJobStatus Status,
    IReadOnlyList<FileActionAffectedLocation> AffectedLocations);

public sealed record FileActionAffectedLocation(
    Guid HostId,
    string DirectoryPath);

public sealed record FileActionJobSnapshot(
    Guid Id,
    FileActionKind Kind,
    FileActionJobStatus Status,
    Guid HostId,
    string HostName,
    string TargetPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string StatusMessage,
    IReadOnlyList<FileActionJobItemSnapshot> Items,
    int CompletedItemCount,
    int FailedItemCount,
    int CancelledItemCount,
    string? ActiveItemName,
    string? FailedItemName,
    string? CurrentDetail,
    string? ActiveSourcePath,
    string? ActiveDestinationPath,
    string? TransferPhase,
    long BytesTransferred,
    long? TotalBytes,
    string? BrowserDownloadToken,
    string? BrowserDownloadFileName,
    string? BrowserDownloadContentType,
    long? BrowserDownloadTotalBytes,
    bool CanStartBrowserDownload)
{
    public int TotalItemCount => Items.Count;
    public int ProcessedItemCount => Items.Count(item => item.Status is
        FileActionItemStatus.Succeeded or
        FileActionItemStatus.Ignored or
        FileActionItemStatus.Cancelled);
    public double ProgressPercent => TotalItemCount == 0
        ? 0
        : TotalBytes.HasValue && TotalBytes.Value > 0
            ? Math.Clamp((double)BytesTransferred / TotalBytes.Value * 100d, 0d, 100d)
            : Math.Clamp((double)(CompletedItemCount + CancelledItemCount) / TotalItemCount * 100d, 0d, 100d);
    public bool UsesByteProgress => TotalBytes.HasValue && TotalBytes.Value > 0;
    public bool RequiresAttention => Status == FileActionJobStatus.NeedsAttention;
    public bool IsTerminal => Status is
        FileActionJobStatus.Succeeded or
        FileActionJobStatus.CompletedWithIssues or
        FileActionJobStatus.Cancelled or
        FileActionJobStatus.Failed;
}

public sealed record FileActionJobItemSnapshot(
    Guid Id,
    string DisplayName,
    string SourcePath,
    string? DestinationPath,
    bool IsDirectory,
    FileActionItemStatus Status,
    string Message,
    string? ResultPath);

public enum FileActionKind
{
    Copy = 0,
    Move = 1,
    Delete = 2,
    CreateFolder = 3,
    Zip = 4,
    Extract = 5,
    Upload = 6,
    Download = 7
}

public enum FileActionJobStatus
{
    Queued = 0,
    Running = 1,
    NeedsAttention = 2,
    Succeeded = 3,
    CompletedWithIssues = 4,
    Cancelled = 5,
    Failed = 6,
    Dismissed = 7
}

public enum FileActionItemStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Ignored = 3,
    Failed = 4,
    Cancelled = 5
}

public enum FileActionFailureDecision
{
    Retry = 0,
    IgnoreAndContinue = 1,
    Cancel = 2
}
