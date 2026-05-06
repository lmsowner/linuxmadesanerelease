using System.Globalization;
using System.Text;
using LinuxMadeSane.Application.Contracts.MediaLibrary;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.MediaLibrary;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Services;

public sealed class MediaLibraryIntegrationService(
    IMediaLibraryIntegrationDataService dataService,
    IMediaLibraryScanQueue scanQueue,
    IPackageManagementService packageManagementService) : IMediaLibraryIntegrationService
{
    private const string FfmpegPackageName = "ffmpeg";

    public async Task<MediaLibraryDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var settingsTask = dataService.GetSettingsAsync(cancellationToken);
        var rootsTask = dataService.ListRootsAsync(cancellationToken);
        var ffmpegTask = dataService.GetFfmpegStatusAsync(cancellationToken);
        var itemsTask = dataService.ListItemsAsync(new MediaItemQuery(MaxItems: int.MaxValue), cancellationToken);

        await Task.WhenAll(settingsTask, rootsTask, ffmpegTask, itemsTask);

        var settings = settingsTask.Result;
        var roots = rootsTask.Result
            .OrderBy(root => root.SortOrder)
            .ThenBy(root => root.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var items = itemsTask.Result;
        var ffmpeg = ffmpegTask.Result;
        var health = ResolveHealth(settings, roots, ffmpeg);

        return new MediaLibraryDashboardSnapshot(
            settings,
            health.State,
            health.Message,
            ffmpeg,
            roots,
            roots.Count(root => root.Enabled),
            items.Count,
            items.Count(item => item.MediaKind == MediaKind.Video),
            items.Count(item => item.MediaKind == MediaKind.Audio),
            items.Count(item => item.MediaKind == MediaKind.Playlist),
            roots
                .Select(root => root.LastScanUtc)
                .Where(value => value.HasValue)
                .OrderByDescending(value => value)
                .FirstOrDefault());
    }

    public async Task<MediaLibrarySettingsEditor> GetSettingsEditorAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dataService.GetSettingsAsync(cancellationToken);
        return new MediaLibrarySettingsEditor
        {
            IsEnabled = settings.IsEnabled,
            StreamingMode = settings.StreamingMode,
            FfmpegPath = settings.FfmpegPath,
            FfprobePath = settings.FfprobePath,
            EnableMetadataProbing = settings.EnableMetadataProbing,
            EnableRemux = settings.EnableRemux,
            EnableTranscoding = settings.EnableTranscoding,
            MaxConcurrentFfmpegJobs = settings.MaxConcurrentFfmpegJobs,
            TempCacheFolder = settings.TempCacheFolder,
            CacheExpiryMinutes = settings.CacheExpiryMinutes,
            MaxCacheSizeBytes = settings.MaxCacheSizeBytes,
            HardwareAcceleration = settings.HardwareAcceleration,
            RequireLoginForPlaylists = settings.RequireLoginForPlaylists,
            RequireLoginForStreams = settings.RequireLoginForStreams,
            AllowLanAnonymousAccess = settings.AllowLanAnonymousAccess,
            GenerateTemporarySignedStreamUrls = settings.GenerateTemporarySignedStreamUrls,
            SignedUrlExpiryMinutes = settings.SignedUrlExpiryMinutes,
            IpAllowlistCsv = settings.IpAllowlistCsv,
            ShowUnsupportedFiles = settings.ShowUnsupportedFiles,
            AllowUnknownBrowserPlayback = settings.AllowUnknownBrowserPlayback
        };
    }

    public async Task SaveSettingsAsync(MediaLibrarySettingsEditor editor, CancellationToken cancellationToken = default)
    {
        var existing = await dataService.GetSettingsAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var settings = existing with
        {
            IsEnabled = editor.IsEnabled,
            StreamingMode = editor.StreamingMode,
            FfmpegPath = editor.FfmpegPath.Trim(),
            FfprobePath = editor.FfprobePath.Trim(),
            EnableMetadataProbing = editor.EnableMetadataProbing,
            EnableRemux = editor.EnableRemux,
            EnableTranscoding = editor.EnableTranscoding,
            MaxConcurrentFfmpegJobs = Math.Clamp(editor.MaxConcurrentFfmpegJobs, 1, 8),
            TempCacheFolder = editor.TempCacheFolder.Trim(),
            CacheExpiryMinutes = Math.Clamp(editor.CacheExpiryMinutes, 15, 10080),
            MaxCacheSizeBytes = Math.Max(0, editor.MaxCacheSizeBytes),
            HardwareAcceleration = editor.HardwareAcceleration,
            RequireLoginForPlaylists = editor.RequireLoginForPlaylists,
            RequireLoginForStreams = editor.RequireLoginForStreams,
            AllowLanAnonymousAccess = editor.AllowLanAnonymousAccess,
            GenerateTemporarySignedStreamUrls = editor.GenerateTemporarySignedStreamUrls,
            SignedUrlExpiryMinutes = Math.Clamp(editor.SignedUrlExpiryMinutes, 5, 1440),
            IpAllowlistCsv = editor.IpAllowlistCsv.Trim(),
            ShowUnsupportedFiles = editor.ShowUnsupportedFiles,
            AllowUnknownBrowserPlayback = editor.AllowUnknownBrowserPlayback,
            UpdatedUtc = now
        };

        await dataService.SaveSettingsAsync(settings, cancellationToken);
    }

    public async Task<MediaLibraryToolInstallResult> InstallFfmpegAsync(CancellationToken cancellationToken = default)
    {
        var actions = new[]
        {
            new PackageAction(
                PackageActionKind.Install,
                FfmpegPackageName,
                "Media Library Streaming uses FFmpeg for metadata probing, remuxing, transcoding, and converted browser preview.",
                false,
                $"apt-get update && apt-get install -y {FfmpegPackageName}")
        };

        var logs = await packageManagementService.ApplyActionsAsync(actions, dryRun: false, cancellationToken);
        var ffmpegStatus = await dataService.GetFfmpegStatusAsync(cancellationToken);
        var commandFailed = logs.Any(log => log.Level == OperationLogLevel.Error);
        var success = ffmpegStatus.FfmpegAvailable && !commandFailed;
        var statusMessage = success
            ? "FFmpeg is installed and available to the Media Library Streaming Integration."
            : ffmpegStatus.FfmpegAvailable
                ? "FFmpeg is now available, but the package install flow reported an error. Check the command logs."
                : "FFmpeg is still missing. Install failed or this LMS process cannot run package installs; check the command logs.";

        return new MediaLibraryToolInstallResult(
            success,
            statusMessage,
            [FfmpegPackageName],
            logs,
            ffmpegStatus);
    }

    public async Task<MediaLibraryRootEditor> GetRootEditorAsync(Guid? rootId, CancellationToken cancellationToken = default)
    {
        if (!rootId.HasValue)
        {
            var roots = await dataService.ListRootsAsync(cancellationToken);
            return new MediaLibraryRootEditor
            {
                Enabled = true,
                Recursive = true,
                SortOrder = roots.Count == 0 ? 0 : roots.Max(root => root.SortOrder) + 10
            };
        }

        var root = await dataService.GetRootAsync(rootId.Value, cancellationToken);
        return root is null
            ? new MediaLibraryRootEditor()
            : new MediaLibraryRootEditor
            {
                Id = root.Id,
                Name = root.Name,
                Path = root.Path,
                Category = root.Category,
                CustomCategoryName = root.CustomCategoryName,
                Enabled = root.Enabled,
                Recursive = root.Recursive,
                IncludeExtensions = string.Join(", ", root.IncludeExtensions),
                ExcludeExtensions = string.Join(", ", root.ExcludeExtensions),
                ExcludeFolders = string.Join(", ", root.ExcludeFolders),
                SortOrder = root.SortOrder,
                Notes = root.Notes
            };
    }

    public async Task<Guid> SaveRootAsync(MediaLibraryRootEditor editor, CancellationToken cancellationToken = default)
    {
        var name = NormalizeRequired(editor.Name, "Enter a display name for this media folder.");
        var path = NormalizePath(editor.Path);
        var now = DateTimeOffset.UtcNow;
        var rootId = editor.Id ?? Guid.NewGuid();
        var existing = editor.Id.HasValue
            ? await dataService.GetRootAsync(editor.Id.Value, cancellationToken)
            : null;

        var root = new MediaLibraryRoot(
            rootId,
            name,
            path,
            editor.Category,
            editor.CustomCategoryName.Trim(),
            editor.Enabled,
            editor.Recursive,
            NormalizeExtensionList(editor.IncludeExtensions),
            NormalizeExtensionList(editor.ExcludeExtensions),
            NormalizeTokenList(editor.ExcludeFolders),
            editor.SortOrder,
            editor.Notes.Trim(),
            existing?.CreatedUtc ?? now,
            now,
            existing?.LastScanUtc,
            existing?.LastScanStatus ?? MediaScanStatus.NotScanned,
            existing?.LastScanMessage ?? string.Empty);

        await dataService.SaveRootAsync(root, cancellationToken);
        return rootId;
    }

    public Task DeleteRootAsync(Guid rootId, CancellationToken cancellationToken = default) =>
        dataService.DeleteRootAsync(rootId, cancellationToken);

    public async Task QueueScanAsync(Guid? rootId = null, CancellationToken cancellationToken = default)
    {
        if (rootId.HasValue)
        {
            var root = await dataService.GetRootAsync(rootId.Value, cancellationToken);
            if (root is not null)
            {
                await dataService.SaveRootAsync(root with
                {
                    LastScanStatus = MediaScanStatus.Queued,
                    LastScanMessage = "Scan queued.",
                    UpdatedUtc = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
        }
        else
        {
            var roots = await dataService.ListRootsAsync(cancellationToken);
            foreach (var root in roots.Where(root => root.Enabled))
            {
                await dataService.SaveRootAsync(root with
                {
                    LastScanStatus = MediaScanStatus.Queued,
                    LastScanMessage = "Scan queued.",
                    UpdatedUtc = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
        }

        await scanQueue.EnqueueScanAsync(rootId, cancellationToken);
    }

    public Task<IReadOnlyList<MediaItem>> ListItemsAsync(MediaItemQuery query, CancellationToken cancellationToken = default) =>
        dataService.ListItemsAsync(query, cancellationToken);

    public Task<MediaItem?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        dataService.GetItemAsync(itemId, cancellationToken);

    public async Task<MediaFilePreviewViewModel?> GetPreviewForPathAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var settings = await dataService.GetSettingsAsync(cancellationToken);
        if (!settings.IsEnabled)
        {
            return null;
        }

        var item = await dataService.GetItemByFullPathAsync(fullPath, cancellationToken);
        if (item is null || item.MediaKind is not (MediaKind.Video or MediaKind.Audio))
        {
            return null;
        }

        var directBrowserPlayback = settings.StreamingMode != MediaStreamingMode.PlaylistOnly &&
                                    (item.IsBrowserCompatible == true || IsBrowserLikelyPlayableByExtension(item.Extension));
        var transcodedPlayback = false;
        var transcodeEnabled = settings.StreamingMode == MediaStreamingMode.TranscodeFallback && settings.EnableTranscoding;
        var ffmpegStatus = !directBrowserPlayback && transcodeEnabled
            ? await dataService.GetFfmpegStatusAsync(cancellationToken)
            : null;
        var canTranscode = transcodeEnabled && ffmpegStatus?.FfmpegAvailable == true;

        var canBrowserPlay = directBrowserPlayback || canTranscode;
        if (!directBrowserPlayback && canTranscode)
        {
            transcodedPlayback = true;
        }

        var message = ResolveBrowserPlaybackMessage(settings, directBrowserPlayback, canTranscode, ffmpegStatus);

        return new MediaFilePreviewViewModel(
            item,
            canBrowserPlay,
            message,
            BuildCompatibilityLabel(item, directBrowserPlayback, transcodedPlayback),
            settings.GenerateTemporarySignedStreamUrls,
            settings.SignedUrlExpiryMinutes,
            transcodedPlayback);
    }

    public Task<IReadOnlyList<MediaItem>> ListPlaylistItemsAsync(MediaPlaylistRequest request, CancellationToken cancellationToken = default)
    {
        var rootId = request.LibraryRootIds.Count == 1 ? request.LibraryRootIds[0] : (Guid?)null;
        var mediaKind = request.MediaKindFilter is MediaKind.Video or MediaKind.Audio
            ? request.MediaKindFilter
            : null;

        return dataService.ListItemsAsync(
            new MediaItemQuery(
                RootId: rootId,
                Category: request.CategoryFilter,
                MediaKind: mediaKind,
                IncludeUnsupported: false,
                SortMode: request.SortMode,
                MaxItems: int.MaxValue),
            cancellationToken);
    }

    public string GeneratePlaylist(MediaPlaylistRequest request, IReadOnlyList<MediaItem> items, Func<MediaItem, string> streamUrlFactory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#EXTM3U");

        foreach (var item in items.Where(static item => item.MediaKind is MediaKind.Video or MediaKind.Audio))
        {
            var groupTitle = GetPlaylistGroupTitle(item);
            var title = Path.GetFileNameWithoutExtension(item.FileName);
            var duration = item.DurationSeconds.HasValue
                ? Math.Max(-1, (int)Math.Round(item.DurationSeconds.Value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)
                : "-1";

            builder.Append("#EXTINF:")
                .Append(duration)
                .Append(" group-title=\"")
                .Append(EscapePlaylistAttribute(groupTitle))
                .Append("\",")
                .AppendLine(title);
            builder.AppendLine(streamUrlFactory(item));
        }

        return builder.ToString();
    }

    private static (MediaLibraryHealthState State, string Message) ResolveHealth(
        MediaLibraryIntegrationSettings settings,
        IReadOnlyList<MediaLibraryRoot> roots,
        FfmpegToolStatus ffmpeg)
    {
        if (!settings.IsEnabled)
        {
            return (MediaLibraryHealthState.Disabled, "The Media Library Streaming Integration is disabled.");
        }

        if (roots.Count == 0 || roots.All(static root => !root.Enabled))
        {
            return (MediaLibraryHealthState.NotConfigured, "Add and enable at least one media folder.");
        }

        if (roots.Any(static root => root.LastScanStatus == MediaScanStatus.PermissionIssue))
        {
            return (MediaLibraryHealthState.PermissionIssue, "At least one media folder could not be read.");
        }

        if (roots.Any(static root => root.LastScanStatus == MediaScanStatus.Failed || root.LastScanStatus == MediaScanStatus.FolderMissing))
        {
            return (MediaLibraryHealthState.ScanFailed, "At least one media scan needs attention.");
        }

        if ((settings.EnableMetadataProbing || settings.EnableRemux || settings.EnableTranscoding ||
             settings.StreamingMode is MediaStreamingMode.RemuxWhenNeeded or MediaStreamingMode.TranscodeFallback) &&
            !ffmpeg.FfmpegAvailable)
        {
            return (MediaLibraryHealthState.FfmpegMissing, "FFmpeg is required for probing, remuxing, or transcoding.");
        }

        return (MediaLibraryHealthState.Ready, "Media playlists and direct VLC streaming are ready.");
    }

    private static string NormalizeRequired(string? value, string errorMessage)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return normalized;
    }

    private static string NormalizePath(string? value)
    {
        var normalized = NormalizeRequired(value, "Enter the physical media folder path.");
        if (!Path.IsPathRooted(normalized))
        {
            throw new InvalidOperationException("Media folder paths must be absolute physical paths.");
        }

        return Path.GetFullPath(normalized);
    }

    private static IReadOnlyList<string> NormalizeExtensionList(string? value) =>
        NormalizeTokenList(value)
            .Select(static item => item.StartsWith(".", StringComparison.Ordinal) ? item : $".{item}")
            .Select(static item => item.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> NormalizeTokenList(string? value) =>
        (value ?? string.Empty)
        .Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsBrowserLikelyPlayableByExtension(string extension) =>
        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);

    private static string ResolveBrowserPlaybackMessage(
        MediaLibraryIntegrationSettings settings,
        bool directBrowserPlayback,
        bool canTranscode,
        FfmpegToolStatus? ffmpegStatus)
    {
        if (directBrowserPlayback)
        {
            return "Direct browser playback is available. Playback will only start when the user presses Play.";
        }

        if (canTranscode)
        {
            return "FFmpeg converted browser playback is available. Preview starts automatically and may begin muted if the browser blocks sound.";
        }

        if (settings.StreamingMode == MediaStreamingMode.PlaylistOnly)
        {
            return "This file is indexed, but browser playback is off because direct streams, remux, and transcode fallback are disabled. Open FFmpeg / Playback Settings and enable the playback paths you want.";
        }

        if (settings.StreamingMode != MediaStreamingMode.TranscodeFallback || !settings.EnableTranscoding)
        {
            return "This file is indexed, but browser playback needs FFmpeg transcoding for this format.";
        }

        return ffmpegStatus?.FfmpegAvailable == true
            ? "This file is indexed, but FFmpeg converted playback is not available for this format."
            : "This file is indexed, but FFmpeg is missing so this format cannot be converted for browser playback.";
    }

    private static string BuildCompatibilityLabel(MediaItem item, bool directBrowserPlayback, bool transcodedPlayback)
    {
        if (directBrowserPlayback)
        {
            return "Browser direct";
        }

        if (transcodedPlayback)
        {
            return "FFmpeg preview";
        }

        if (item.IsBrowserCompatible == true)
        {
            return "Browser compatible";
        }

        if (item.RequiresRemux == true)
        {
            return "Needs remux";
        }

        if (item.RequiresTranscode == true)
        {
            return "Needs transcode";
        }

        return item.IsVlcCompatible == true ? "VLC compatible" : "Compatibility unknown";
    }

    private static string GetPlaylistGroupTitle(MediaItem item) =>
        item.LibraryCategory == MediaLibraryCategory.Custom && !string.IsNullOrWhiteSpace(item.LibraryRootName)
            ? item.LibraryRootName
            : item.LibraryCategory.ToString();

    private static string EscapePlaylistAttribute(string value) =>
        value.Replace("\"", "'", StringComparison.Ordinal);
}
