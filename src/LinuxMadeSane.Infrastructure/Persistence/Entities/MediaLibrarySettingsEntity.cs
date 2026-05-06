namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class MediaLibrarySettingsEntity
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; }
    public int StreamingMode { get; set; }
    public string FfmpegPath { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
    public bool EnableMetadataProbing { get; set; }
    public bool EnableRemux { get; set; }
    public bool EnableTranscoding { get; set; }
    public int MaxConcurrentFfmpegJobs { get; set; }
    public string TempCacheFolder { get; set; } = string.Empty;
    public int CacheExpiryMinutes { get; set; }
    public long MaxCacheSizeBytes { get; set; }
    public int HardwareAcceleration { get; set; }
    public bool RequireLoginForPlaylists { get; set; }
    public bool RequireLoginForStreams { get; set; }
    public bool AllowLanAnonymousAccess { get; set; }
    public bool GenerateTemporarySignedStreamUrls { get; set; }
    public int SignedUrlExpiryMinutes { get; set; }
    public string IpAllowlistCsv { get; set; } = string.Empty;
    public bool ShowUnsupportedFiles { get; set; }
    public bool AllowUnknownBrowserPlayback { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
