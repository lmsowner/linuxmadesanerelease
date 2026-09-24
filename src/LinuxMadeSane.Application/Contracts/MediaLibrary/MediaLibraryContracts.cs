// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Models.MediaLibrary;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.MediaLibrary;

public sealed class MediaLibraryRootEditor
{
    public Guid? Id { get; set; }

    [Required]
    [StringLength(160)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(1024)]
    public string Path { get; set; } = string.Empty;

    public MediaLibraryCategory Category { get; set; } = MediaLibraryCategory.Mixed;

    [StringLength(160)]
    public string CustomCategoryName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool Recursive { get; set; } = true;

    [StringLength(1024)]
    public string IncludeExtensions { get; set; } = string.Empty;

    [StringLength(1024)]
    public string ExcludeExtensions { get; set; } = string.Empty;

    [StringLength(2048)]
    public string ExcludeFolders { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    [StringLength(1024)]
    public string Notes { get; set; } = string.Empty;
}

public sealed class MediaLibrarySettingsEditor
{
    public bool IsEnabled { get; set; }

    public MediaStreamingMode StreamingMode { get; set; } = MediaStreamingMode.PlaylistOnly;

    [StringLength(1024)]
    public string FfmpegPath { get; set; } = string.Empty;

    [StringLength(1024)]
    public string FfprobePath { get; set; } = string.Empty;

    public bool EnableMetadataProbing { get; set; }

    public bool EnableRemux { get; set; }

    public bool EnableTranscoding { get; set; }

    [Range(1, 8)]
    public int MaxConcurrentFfmpegJobs { get; set; } = 1;

    [StringLength(1024)]
    public string TempCacheFolder { get; set; } = string.Empty;

    [Range(15, 10080)]
    public int CacheExpiryMinutes { get; set; } = 240;

    [Range(0, long.MaxValue)]
    public long MaxCacheSizeBytes { get; set; } = 10L * 1024L * 1024L * 1024L;

    public MediaHardwareAcceleration HardwareAcceleration { get; set; } = MediaHardwareAcceleration.None;

    public bool RequireLoginForPlaylists { get; set; } = true;

    public bool RequireLoginForStreams { get; set; } = true;

    public bool AllowLanAnonymousAccess { get; set; }

    public bool GenerateTemporarySignedStreamUrls { get; set; } = true;

    [Range(5, 1440)]
    public int SignedUrlExpiryMinutes { get; set; } = 120;

    [StringLength(2048)]
    public string IpAllowlistCsv { get; set; } = string.Empty;

    public bool ShowUnsupportedFiles { get; set; }

    public bool AllowUnknownBrowserPlayback { get; set; }
}

public sealed record MediaFilePreviewViewModel(
    MediaItem Item,
    bool CanBrowserPlay,
    string BrowserPlaybackMessage,
    string CompatibilityLabel,
    bool UseSignedStreamUrl,
    int SignedUrlExpiryMinutes,
    bool UsesTranscodedPlayback);

public sealed record MediaLibraryToolInstallResult(
    bool Success,
    string StatusMessage,
    IReadOnlyList<string> PackageNames,
    IReadOnlyList<OperationLogEntry> OperationLogs,
    FfmpegToolStatus FfmpegStatus);
