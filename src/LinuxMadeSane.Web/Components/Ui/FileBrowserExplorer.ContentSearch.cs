// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace LinuxMadeSane.Web.Components.Ui;

public partial class FileBrowserExplorer
{
    [Parameter] public Func<FileSearchRequest, IProgress<FileSearchProgress>?, CancellationToken, Task<FileSearchResponse>>? ContentSearchRequested { get; set; }
    [Parameter] public EventCallback<IReadOnlyList<SftpItem>> ContentSearchItemsChanged { get; set; }
    [Parameter] public string? ContentSearchScope { get; set; }
    [Parameter] public bool ShowContentSearch { get; set; } = true;
    private string contentQuery = "";
    private bool contentMatchCase;
    private bool contentIncludeSubfolders;
    private readonly Dictionary<string, SftpItem> contentAdditionalItems = new(StringComparer.Ordinal);
    private HashSet<string>? contentMatchPaths;
    private CancellationTokenSource? contentSearchCts;
    private string? contentSearchFeedback;
    private string contentSearchTone = "info";
    private IReadOnlyList<SftpItem>? previousContentItems;
    private string? previousContentPath, previousContentScope;
    private string ContentEmptyMessage => contentSearchCts is not null
        ? "Searching the displayed PDF, text and log files…"
        : "No matching PDF, text or log files. Clear the content search to show the other files.";

    protected override void OnParametersSet()
    {
        if (previousContentItems is not null && (!ReferenceEquals(previousContentItems, Items) ||
            previousContentPath != CurrentPath || previousContentScope != ContentSearchScope))
            ResetContentSearch(clearQuery: true);
        previousContentItems = Items; previousContentPath = CurrentPath; previousContentScope = ContentSearchScope;
    }

    private IEnumerable<SftpItem> BuildNameFilteredItems()
    {
        var filter = itemFilter.Trim();
        return Items.Where(item => filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void ResetContentSearch(bool clearQuery)
    {
        var running = contentSearchCts; contentSearchCts = null;
        if (running is not null) _ = running.CancelAsync();
        contentMatchPaths = null; contentSearchFeedback = null;
        contentAdditionalItems.Clear();
        _ = ContentSearchItemsChanged.InvokeAsync(Array.Empty<SftpItem>());
        if (clearQuery) contentQuery = "";
    }

    private Task ClearContentSearchAsync()
    {
        ResetContentSearch(clearQuery: true);
        return Task.CompletedTask;
    }

    private Task OnContentQueryChangedAsync(string? value)
    {
        ResetContentSearch(clearQuery: false);
        contentQuery = value ?? "";
        return Task.CompletedTask;
    }

    private Task OnContentMatchCaseChangedAsync(bool value)
    {
        ResetContentSearch(clearQuery: false);
        contentMatchCase = value;
        return Task.CompletedTask;
    }

    private Task OnContentSubfoldersChangedAsync(bool value)
    {
        ResetContentSearch(clearQuery: false);
        contentIncludeSubfolders = value;
        return Task.CompletedTask;
    }

    private string? ContentMatchLocation(SftpItem item)
    {
        if (!contentAdditionalItems.ContainsKey(item.FullPath)) return null;
        var parent = GetParentPath(item.FullPath, null) ?? "/";
        return parent.StartsWith(CurrentPath.TrimEnd('/') + "/", StringComparison.Ordinal)
            ? parent[(CurrentPath.TrimEnd('/').Length + 1)..] : parent;
    }

    private Task HandleContentSearchKeyAsync(KeyboardEventArgs args) => args.Key switch
    {
        "Enter" => RunContentSearchAsync(),
        "Escape" => ClearContentSearchAsync(),
        _ => Task.CompletedTask
    };

    private Task CancelContentSearchAsync() => contentSearchCts?.CancelAsync() ?? Task.CompletedTask;

    private async Task RunContentSearchAsync()
    {
        if (!CanBrowse || IsBusy || contentSearchCts is not null || ContentSearchRequested is null || !ShowContentSearch) return;
        var query = contentQuery.Trim();
        var caseInsensitive = !contentMatchCase;
        var includeSubfolders = contentIncludeSubfolders;
        var nameFilter = itemFilter.Trim();
        if (query.Length == 0) { ResetContentSearch(clearQuery: true); return; }
        // Snapshot the name-filtered folder, not the previous content result. A new query can broaden results.
        var candidates = BuildNameFilteredItems().Where(item => item.ItemType == SftpItemType.File).ToArray();
        var allowedPaths = candidates.Select(item => item.FullPath).ToHashSet(StringComparer.Ordinal);
        var originals = Items.ToDictionary(item => item.FullPath, StringComparer.Ordinal);
        var folder = CurrentPath;
        using var cancellation = new CancellationTokenSource();
        contentSearchCts = cancellation;
        contentMatchPaths = new(StringComparer.Ordinal);
        contentAdditionalItems.Clear();
        contentSearchTone = "info";
        contentSearchFeedback = includeSubfolders ? "Searching this folder and subfolders…" : $"Searching displayed files… 0 of {candidates.Length}.";
        var checkedFiles = 0;
        string? warning = null;
        var lastPaint = DateTime.UtcNow;
        try
        {
            // Hidden files must not remain selected for destructive toolbar actions.
            await MarqueeSelectionChanged.InvokeAsync(Array.Empty<string>());
            await InvokeAsync(StateHasChanged);
            var requests = includeSubfolders
                ? new[] { new FileSearchRequest(folder, IncludeFolders: false, ContainsText: query,
                    CaseInsensitive: caseInsensitive, MaxResults: 2000, IncludeSubfolders: true) { NameContains = nameFilter } }
                : candidates.Chunk(64).Select(batch => new FileSearchRequest(folder, IncludeFolders: false, ContainsText: query,
                    CaseInsensitive: caseInsensitive, MaxResults: batch.Length + 1, IncludeSubfolders: false, CandidateNames: batch.Select(item => item.Name).ToArray()));
            void AcceptMatch(FileSearchMatch match)
            {
                if (match.ItemType != SftpItemType.File || (nameFilter.Length > 0 && !match.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))) return;
                if (!allowedPaths.Contains(match.FullPath) && (!includeSubfolders || !match.FullPath.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal))) return;
                contentMatchPaths!.Add(match.FullPath);
                if (!originals.ContainsKey(match.FullPath))
                    contentAdditionalItems[match.FullPath] = new(match.Name, match.FullPath, match.ItemType, match.SizeBytes, match.LastModifiedUtc, "");
            }
            foreach (var searchRequest in requests)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var progress = new Progress<FileSearchProgress>(update =>
                {
                    if (contentSearchCts != cancellation || cancellation.IsCancellationRequested) return;
                    _ = InvokeAsync(() =>
                    {
                        if (contentSearchCts != cancellation || cancellation.IsCancellationRequested) return;
                        if (update.Match is { } match) AcceptMatch(match);
                        if (!string.IsNullOrWhiteSpace(update.StatusMessage))
                        {
                            contentSearchFeedback = update.StatusMessage;
                            StateHasChanged();
                            return;
                        }
                        if (DateTime.UtcNow - lastPaint < TimeSpan.FromMilliseconds(250)) return;
                        lastPaint = DateTime.UtcNow;
                        _ = ContentSearchItemsChanged.InvokeAsync(contentAdditionalItems.Values.ToArray());
                        contentSearchFeedback = includeSubfolders ? $"Searching subfolders… {contentMatchPaths!.Count} matches." : $"Searching displayed files… {checkedFiles} of {candidates.Length}, {contentMatchPaths!.Count} matches.";
                        StateHasChanged();
                    });
                });
                var response = await ContentSearchRequested(searchRequest, progress, cancellation.Token).WaitAsync(cancellation.Token);
                if (contentSearchCts != cancellation) return;
                foreach (var match in response.Results) AcceptMatch(match);
                await ContentSearchItemsChanged.InvokeAsync(contentAdditionalItems.Values.ToArray());
                warning ??= response.Warning;
                checkedFiles += searchRequest.CandidateNames?.Count ?? 0;
                if (response.LimitReached) warning = "The result limit was reached. Narrow your search to see the other matches.";
                contentSearchFeedback = includeSubfolders ? $"Searching subfolders… {contentMatchPaths!.Count} matches."
                    : $"Searching displayed files… {checkedFiles} of {candidates.Length}, {contentMatchPaths!.Count} matches.";
                await InvokeAsync(StateHasChanged);
            }
            contentSearchFeedback = $"{contentMatchPaths!.Count} matching document{(contentMatchPaths.Count == 1 ? "" : "s")} · PDF, text and logs · {(includeSubfolders ? "including subfolders" : "current folder only")}.";
            contentSearchTone = "success";
            if (!string.IsNullOrWhiteSpace(warning))
            {
                contentSearchFeedback += " Search incomplete: " + warning;
                contentSearchTone = "warning";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (contentSearchCts != cancellation) return;
            contentSearchFeedback = $"Content search cancelled · {contentMatchPaths!.Count} matches found.";
            contentSearchTone = "warning";
        }
        catch (Exception exception)
        {
            if (contentSearchCts != cancellation) return;
            contentSearchFeedback = "Content search incomplete: " + exception.Message;
            contentSearchTone = "warning";
        }
        finally
        {
            if (contentSearchCts == cancellation)
            {
                await ContentSearchItemsChanged.InvokeAsync(contentAdditionalItems.Values.ToArray());
                contentSearchCts = null;
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}
