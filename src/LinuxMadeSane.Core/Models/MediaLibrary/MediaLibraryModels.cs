// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.MediaLibrary;

public enum MediaLibraryCategory
{
    Movies = 0,
    TV = 1,
    Music = 2,
    Photos = 3,
    Mixed = 4,
    Custom = 5
}

public enum MediaKind
{
    Video = 0,
    Audio = 1,
    Playlist = 2,
    Other = 3
}

public enum MediaStreamingMode
{
    PlaylistOnly = 0,
    DirectStream = 1,
    RemuxWhenNeeded = 2,
    TranscodeFallback = 3
}

public enum MediaLibraryHealthState
{
    Disabled = 0,
    NotConfigured = 1,
    Ready = 2,
    FfmpegMissing = 3,
    ScanFailed = 4,
    PermissionIssue = 5
}

public enum MediaScanStatus
{
    NotScanned = 0,
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    PermissionIssue = 5,
    FolderMissing = 6
}

public enum MediaPlaylistOutputFormat
{
    M3U = 0,
    M3U8 = 1
}

public enum MediaSortMode
{
    Name = 0,
    Path = 1,
    LastModifiedDescending = 2,
    LastModifiedAscending = 3,
    SizeDescending = 4
}

public enum MediaHardwareAcceleration
{
    None = 0,
    VAAPI = 1,
    NVENC = 2,
    QuickSync = 3,
    AutoDetect = 4
}

public sealed record MediaLibraryIntegrationSettings(
    bool IsEnabled,
    MediaStreamingMode StreamingMode,
    string FfmpegPath,
    string FfprobePath,
    bool EnableMetadataProbing,
    bool EnableRemux,
    bool EnableTranscoding,
    int MaxConcurrentFfmpegJobs,
    string TempCacheFolder,
    int CacheExpiryMinutes,
    long MaxCacheSizeBytes,
    MediaHardwareAcceleration HardwareAcceleration,
    bool RequireLoginForPlaylists,
    bool RequireLoginForStreams,
    bool AllowLanAnonymousAccess,
    bool GenerateTemporarySignedStreamUrls,
    int SignedUrlExpiryMinutes,
    string IpAllowlistCsv,
    bool ShowUnsupportedFiles,
    bool AllowUnknownBrowserPlayback,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public static MediaLibraryIntegrationSettings CreateDefault(DateTimeOffset now) =>
        new(
            IsEnabled: false,
            StreamingMode: MediaStreamingMode.PlaylistOnly,
            FfmpegPath: string.Empty,
            FfprobePath: string.Empty,
            EnableMetadataProbing: false,
            EnableRemux: false,
            EnableTranscoding: false,
            MaxConcurrentFfmpegJobs: 1,
            TempCacheFolder: string.Empty,
            CacheExpiryMinutes: 240,
            MaxCacheSizeBytes: 10L * 1024L * 1024L * 1024L,
            HardwareAcceleration: MediaHardwareAcceleration.None,
            RequireLoginForPlaylists: true,
            RequireLoginForStreams: true,
            AllowLanAnonymousAccess: false,
            GenerateTemporarySignedStreamUrls: true,
            SignedUrlExpiryMinutes: 120,
            IpAllowlistCsv: string.Empty,
            ShowUnsupportedFiles: false,
            AllowUnknownBrowserPlayback: false,
            CreatedUtc: now,
            UpdatedUtc: now);
}

public sealed record MediaLibraryRoot(
    Guid Id,
    string Name,
    string Path,
    MediaLibraryCategory Category,
    string CustomCategoryName,
    bool Enabled,
    bool Recursive,
    IReadOnlyList<string> IncludeExtensions,
    IReadOnlyList<string> ExcludeExtensions,
    IReadOnlyList<string> ExcludeFolders,
    int SortOrder,
    string Notes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? LastScanUtc,
    MediaScanStatus LastScanStatus,
    string LastScanMessage);

public sealed record MediaItem(
    Guid Id,
    Guid LibraryRootId,
    string LibraryRootName,
    MediaLibraryCategory LibraryCategory,
    string RelativePath,
    string FullPath,
    string FileName,
    string Extension,
    MediaKind MediaKind,
    long SizeBytes,
    DateTimeOffset? LastModifiedUtc,
    string MimeType,
    double? DurationSeconds,
    string? VideoCodec,
    string? AudioCodec,
    string? Container,
    bool? IsBrowserCompatible,
    bool? IsVlcCompatible,
    bool? RequiresRemux,
    bool? RequiresTranscode,
    DateTimeOffset IndexedUtc);

public sealed record MediaItemQuery(
    Guid? RootId = null,
    MediaLibraryCategory? Category = null,
    MediaKind? MediaKind = null,
    string? Search = null,
    string? Extension = null,
    bool IncludeUnsupported = false,
    MediaSortMode SortMode = MediaSortMode.Name,
    int MaxItems = 500);

public sealed record MediaPlaylistRequest(
    IReadOnlyList<Guid> LibraryRootIds,
    MediaLibraryCategory? CategoryFilter,
    MediaKind? MediaKindFilter,
    bool IncludeSubfolders,
    MediaSortMode SortMode,
    MediaPlaylistOutputFormat OutputFormat);

public sealed record MediaLibraryScanResult(
    Guid? LibraryRootId,
    MediaScanStatus Status,
    int RootsScanned,
    int ItemsIndexed,
    int ItemsUpdated,
    int ItemsRemoved,
    int ItemsSkipped,
    string Message,
    DateTimeOffset CompletedUtc);

public sealed record FfmpegToolStatus(
    bool FfmpegAvailable,
    string FfmpegPath,
    string FfmpegVersion,
    bool FfprobeAvailable,
    string FfprobePath,
    string FfprobeVersion,
    string Message);

public sealed record MediaLibraryDashboardSnapshot(
    MediaLibraryIntegrationSettings Settings,
    MediaLibraryHealthState HealthState,
    string HealthMessage,
    FfmpegToolStatus FfmpegStatus,
    IReadOnlyList<MediaLibraryRoot> Roots,
    int EnabledRootCount,
    int MediaItemCount,
    int VideoItemCount,
    int AudioItemCount,
    int PlaylistItemCount,
    DateTimeOffset? LastScanUtc);

public sealed record MediaStreamOpenResult(
    MediaItem Item,
    Stream ContentStream,
    string ContentType,
    DateTimeOffset? LastModifiedUtc);
