// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Linq;

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed class FileBrowserWorkspaceRegistry
{
    private readonly ConcurrentDictionary<string, FileBrowserWorkspaceState> workspaces = new(StringComparer.Ordinal);

    public FileBrowserWorkspaceState GetOrCreate(string workspaceId) =>
        workspaces.GetOrAdd(workspaceId, _ => new FileBrowserWorkspaceState());

    public FileBrowserWorkspaceLookupResult? FindDetachedBrowser(Guid browserId)
    {
        foreach (var entry in workspaces)
        {
            var state = entry.Value.GetDetachedBrowser(browserId);
            if (state is not null)
            {
                return new FileBrowserWorkspaceLookupResult(entry.Key, entry.Value, state, null);
            }
        }

        return null;
    }

    public FileBrowserWorkspaceLookupResult? FindDetachedSearch(Guid searchId)
    {
        foreach (var entry in workspaces)
        {
            var state = entry.Value.GetDetachedSearch(searchId);
            if (state is not null)
            {
                return new FileBrowserWorkspaceLookupResult(entry.Key, entry.Value, null, state);
            }
        }

        return null;
    }
}

public sealed record FileBrowserWorkspaceLookupResult(
    string WorkspaceId,
    FileBrowserWorkspaceState Workspace,
    DetachedFileBrowserState? BrowserState,
    DetachedFileSearchState? SearchState);

public sealed class FileBrowserWorkspaceState
{
    private readonly Dictionary<Guid, DetachedFileBrowserState> detachedBrowsers = [];
    private readonly Dictionary<Guid, DetachedFileSearchState> detachedSearches = [];
    private readonly object syncRoot = new();

    public DetachedFileBrowserState CreateDetachedBrowser(DetachedFileBrowserSnapshot snapshot)
    {
        lock (syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            var state = new DetachedFileBrowserState(
                Guid.NewGuid(),
                snapshot.HostId,
                snapshot.CurrentPath,
                snapshot.SelectedPath,
                snapshot.Username,
                snapshot.SecretHandle,
                snapshot.PreferStoredCredentials,
                snapshot.PreviewOpen,
                snapshot.PreviewFocused,
                snapshot.PreviewDock,
                snapshot.PreviewPanelSizePx,
                snapshot.FoldersPaneCollapsed,
                now,
                now);

            detachedBrowsers[state.BrowserId] = state;
            return state;
        }
    }

    public DetachedFileBrowserState? GetDetachedBrowser(Guid browserId)
    {
        lock (syncRoot)
        {
            detachedBrowsers.TryGetValue(browserId, out var state);
            return state;
        }
    }

    public bool UpdateDetachedBrowser(Guid browserId, DetachedFileBrowserSnapshot snapshot)
    {
        lock (syncRoot)
        {
            if (!detachedBrowsers.TryGetValue(browserId, out var existing))
            {
                return false;
            }

            detachedBrowsers[browserId] = existing with
            {
                HostId = snapshot.HostId,
                CurrentPath = snapshot.CurrentPath,
                SelectedPath = snapshot.SelectedPath,
                Username = snapshot.Username,
                SecretHandle = snapshot.SecretHandle,
                PreferStoredCredentials = snapshot.PreferStoredCredentials,
                PreviewOpen = snapshot.PreviewOpen,
                PreviewFocused = snapshot.PreviewFocused,
                PreviewDock = snapshot.PreviewDock,
                PreviewPanelSizePx = snapshot.PreviewPanelSizePx,
                FoldersPaneCollapsed = snapshot.FoldersPaneCollapsed,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            return true;
        }
    }

    public bool RemoveDetachedBrowser(Guid browserId)
    {
        lock (syncRoot)
        {
            return detachedBrowsers.Remove(browserId);
        }
    }

    public DetachedFileSearchState CreateDetachedSearch(DetachedFileSearchSnapshot snapshot)
    {
        lock (syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            var state = new DetachedFileSearchState(
                Guid.NewGuid(),
                snapshot.HostId,
                snapshot.RootPath,
                snapshot.Username,
                snapshot.SecretHandle,
                snapshot.PreferStoredCredentials,
                snapshot.PathSuggestions,
                now,
                now);

            detachedSearches[state.SearchId] = state;
            return state;
        }
    }

    public DetachedFileSearchState? GetDetachedSearch(Guid searchId)
    {
        lock (syncRoot)
        {
            detachedSearches.TryGetValue(searchId, out var state);
            return state;
        }
    }

    public bool UpdateDetachedSearch(Guid searchId, DetachedFileSearchSnapshot snapshot)
    {
        lock (syncRoot)
        {
            if (!detachedSearches.TryGetValue(searchId, out var existing))
            {
                return false;
            }

            detachedSearches[searchId] = existing with
            {
                HostId = snapshot.HostId,
                RootPath = snapshot.RootPath,
                Username = snapshot.Username,
                SecretHandle = snapshot.SecretHandle,
                PreferStoredCredentials = snapshot.PreferStoredCredentials,
                PathSuggestions = snapshot.PathSuggestions,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            return true;
        }
    }

    public bool RemoveDetachedSearch(Guid searchId)
    {
        lock (syncRoot)
        {
            return detachedSearches.Remove(searchId);
        }
    }

    public FileBrowserClipboardState SetClipboard(FileBrowserClipboardSnapshot snapshot)
    {
        lock (syncRoot)
        {
            var items = snapshot.Items
                .Select(item => new FileBrowserClipboardItemState(item.SourcePath, item.DisplayName, item.IsDirectory, item.SizeBytes))
                .ToArray();

            Clipboard = new FileBrowserClipboardState(
                snapshot.Operation,
                snapshot.Host,
                items,
                snapshot.Username,
                snapshot.SecretHandle,
                snapshot.PreferStoredCredentials,
                DateTimeOffset.UtcNow);

            return Clipboard;
        }
    }

    public void ClearClipboard()
    {
        lock (syncRoot)
        {
            Clipboard = null;
        }
    }

    public FileBrowserClipboardState? Clipboard { get; private set; }
}

public sealed record DetachedFileBrowserSnapshot(
    Guid HostId,
    string CurrentPath,
    string SelectedPath,
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    bool PreviewOpen,
    bool PreviewFocused,
    FileBrowserPreviewDock PreviewDock,
    int PreviewPanelSizePx,
    bool FoldersPaneCollapsed);

public sealed record DetachedFileBrowserState(
    Guid BrowserId,
    Guid HostId,
    string CurrentPath,
    string SelectedPath,
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    bool PreviewOpen,
    bool PreviewFocused,
    FileBrowserPreviewDock PreviewDock,
    int PreviewPanelSizePx,
    bool FoldersPaneCollapsed,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DetachedFileSearchSnapshot(
    Guid HostId,
    string RootPath,
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    IReadOnlyList<string> PathSuggestions);

public sealed record DetachedFileSearchState(
    Guid SearchId,
    Guid HostId,
    string RootPath,
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    IReadOnlyList<string> PathSuggestions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public enum FileBrowserPreviewDock
{
    Right = 0,
    Bottom = 1
}

public enum FileBrowserClipboardOperation
{
    Copy = 0,
    Cut = 1
}

public sealed record FileBrowserClipboardItemSnapshot(
    string SourcePath,
    string DisplayName,
    bool IsDirectory,
    long? SizeBytes = null);

public sealed record FileBrowserClipboardSnapshot(
    FileBrowserClipboardOperation Operation,
    ManagedHost Host,
    IReadOnlyList<FileBrowserClipboardItemSnapshot> Items,
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials);

public sealed record FileBrowserClipboardItemState(
    string SourcePath,
    string DisplayName,
    bool IsDirectory,
    long? SizeBytes = null);

public sealed record FileBrowserClipboardState(
    FileBrowserClipboardOperation Operation,
    ManagedHost Host,
    IReadOnlyList<FileBrowserClipboardItemState> Items,
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    DateTimeOffset CapturedAtUtc)
{
    public Guid HostId => Host.Id;
    public string HostName => Host.Name;
    public int ItemCount => Items.Count;
    public string DisplayName => ItemCount == 1 ? Items[0].DisplayName : $"{ItemCount} items";
    public string SourcePath => ItemCount == 1 ? Items[0].SourcePath : $"{ItemCount} items";
    public bool IsDirectory => ItemCount == 1 && Items[0].IsDirectory;
}
