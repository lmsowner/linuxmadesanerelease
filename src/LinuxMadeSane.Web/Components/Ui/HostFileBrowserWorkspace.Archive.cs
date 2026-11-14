// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using Microsoft.JSInterop;

namespace LinuxMadeSane.Web.Components.Ui;

public partial class HostFileBrowserWorkspace
{
    private string archiveDirectory = "";
    private readonly HashSet<string> archiveSelection = new(StringComparer.Ordinal);
    private bool archiveExtracting;
    private string archiveFeedback = "";
    private bool CanDragArchive => !IsFilePicker && previewArchiveFormat is not null;

    internal static IReadOnlyList<ArchiveEntry> ArchiveChildren(IEnumerable<ArchiveEntry> entries, string directory)
    {
        var prefix = directory.Length == 0 ? "" : directory.TrimEnd('/') + "/";
        var children = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var path = entry.FullName;
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            path = path.TrimEnd('/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length <= prefix.Length) continue;
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/');
            var name = slash < 0 ? rest : rest[..slash];
            if (name is "" or "." or "..") continue;
            var child = slash < 0 ? entry with { Name = name, FullName = path } : new ArchiveEntry(name, prefix + name, SftpItemType.Folder, 0, null);
            if (!children.TryGetValue(name, out var existing) || child.ItemType == SftpItemType.Folder)
                children[name] = child;
        }
        return children.Values.OrderBy(e => e.ItemType != SftpItemType.Folder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<ArchiveEntry> VisibleArchiveEntries => ArchiveChildren(previewArchiveEntries, archiveDirectory);
    private void OpenArchiveDirectory(ArchiveEntry entry)
    {
        if (entry.ItemType != SftpItemType.Folder) return;
        archiveDirectory = entry.FullName;
        archiveSelection.Clear();
    }
    private void ArchiveUp()
    {
        var slash = archiveDirectory.LastIndexOf('/');
        archiveDirectory = slash < 0 ? "" : archiveDirectory[..slash];
        archiveSelection.Clear();
    }
    private void ToggleArchiveEntry(string path)
    {
        if (!archiveSelection.Add(path)) archiveSelection.Remove(path);
    }
    private Task ExtractArchiveHereAsync() => ExtractArchiveDropAsync(previewLoadedPath, archiveSelection.ToArray(), currentPath);

    [JSInvokable]
    public async Task ExtractArchiveDropAsync(string source, string[] entries, string destination)
    {
        if (browser is null || !canBrowse || isBusy || archiveExtracting || !CanDragArchive || source != previewLoadedPath) return;
        archiveExtracting = true;
        archiveFeedback = "Extracting archive contents…";
        SetOperationMessage(archiveFeedback, "info");
        await InvokeAsync(StateHasChanged);
        try
        {
            await FileAccessService.ExtractArchiveSelectionAsync(browser.Host, source, ResolveBrowserPath(destination), entries, BuildConnectionProfile());
            archiveFeedback = $"Extracted to {destination}.";
            SetOperationMessage(archiveFeedback, "success");
            await RefreshCurrentListingPreserveSelectionAsync(currentPath);
        }
        catch (Exception exception)
        {
            archiveFeedback = $"Extraction failed: {exception.Message} Any files already extracted remain in {destination}.";
            SetOperationMessage(archiveFeedback, "error");
        }
        finally
        {
            archiveExtracting = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}
