// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
namespace LinuxMadeSane.Web.Components.Ui;

public partial class HostFileBrowserWorkspace
{
    private readonly Dictionary<string, (string Url, DateTimeOffset Expires)> desktopDownloadUrls = new();

    private void DesktopDownloadRequested() => archiveFeedback = "Download requested. Check your browser’s downloads for progress.";

    private string? DesktopFileDownload(SftpItem item) => browser is null || IsFilePicker || item.ItemType != SftpItemType.File
        ? null : DesktopDownloadUrl(item.FullPath, item.Name, null, false);

    private string? ArchiveDesktopDownload(ArchiveEntry? entry = null, bool all = false)
    {
        if (browser is null || previewArchiveFormat != ArchiveFormat.Zip || string.IsNullOrEmpty(previewLoadedPath)) return null;
        var paths = all ? [] : entry is null ? archiveSelection.ToArray() : archiveSelection.Contains(entry.FullName) ? archiveSelection.ToArray() : [entry.FullName];
        var single = paths.Length == 1 ? previewArchiveEntries.FirstOrDefault(e => NormalizeDesktopZipPath(e.FullName) == paths[0]) : null;
        var asZip = single is null || single.ItemType == SftpItemType.Folder;
        var name = asZip ? (paths.Length == 1 ? paths[0].TrimEnd('/').Split('/')[^1] : "archive-contents") + ".zip" : single!.Name;
        return DesktopDownloadUrl(previewLoadedPath, name, paths, asZip);
    }

    private string DesktopDownloadUrl(string source, string name, string[]? paths, bool asZip)
    {
        var key = System.Text.Json.JsonSerializer.Serialize(new { source, name, paths, asZip, profile = BuildConnectionProfile() });
        if (desktopDownloadUrls.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Url;
        var url = BrowserTransfers.RegisterDesktopDownload(FileAccessService, browser!.Host, BuildConnectionProfile(), ResolveBrowserPath(source), name, paths, asZip);
        var absolute = NavigationManager.ToAbsoluteUri(url).AbsoluteUri;
        desktopDownloadUrls[key] = (absolute, DateTimeOffset.UtcNow.AddMinutes(25));
        return absolute;
    }

    private static string NormalizeDesktopZipPath(string value)
    {
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value.TrimEnd('/');
    }

    private string? ArchiveDesktopFileName(ArchiveEntry? entry = null, bool all = false)
    {
        var paths = all ? [] : entry is null ? archiveSelection.ToArray() : archiveSelection.Contains(entry.FullName) ? archiveSelection.ToArray() : [entry.FullName];
        var single = paths.Length == 1 ? previewArchiveEntries.FirstOrDefault(e => NormalizeDesktopZipPath(e.FullName) == paths[0]) : null;
        return single is { ItemType: not SftpItemType.Folder } ? single.Name : (paths.Length == 1 ? paths[0].TrimEnd('/').Split('/')[^1] : "archive-contents") + ".zip";
    }
}
