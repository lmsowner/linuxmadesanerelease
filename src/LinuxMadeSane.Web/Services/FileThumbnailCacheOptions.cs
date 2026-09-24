// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Web.Services;

public sealed class FileThumbnailCacheOptions
{
    public const string SectionName = "FileBrowserThumbnails";

    public string CacheDirectory { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 7;
    public int CleanupIntervalMinutes { get; set; } = 360;
    public int MaxCacheMegabytes { get; set; } = 512;
    public int MaxSourceMegabytes { get; set; } = 64;
    public int MaxDimensionPixels { get; set; } = 640;
    public int GenerationConcurrency { get; set; } = 3;
    public int GenerationTimeoutSeconds { get; set; } = 30;

    public TimeSpan Retention => TimeSpan.FromDays(Math.Clamp(RetentionDays, 1, 90));
    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(Math.Clamp(CleanupIntervalMinutes, 15, 10_080));
    public long MaxCacheBytes => Math.Clamp(MaxCacheMegabytes, 1, 16_384) * 1024L * 1024L;
    public int MaxSourceBytes => Math.Clamp(MaxSourceMegabytes, 1, 64) * 1024 * 1024;
    public int MaxDimension => Math.Clamp(MaxDimensionPixels, 160, 1_600);
    public int ConcurrentGenerationLimit => Math.Clamp(GenerationConcurrency, 1, 8);
    public TimeSpan GenerationTimeout => TimeSpan.FromSeconds(Math.Clamp(GenerationTimeoutSeconds, 5, 120));

    public static FileThumbnailCacheOptions FromConfiguration(
        IConfiguration configuration,
        string contentRootPath)
    {
        var options = configuration.GetSection(SectionName).Get<FileThumbnailCacheOptions>() ?? new FileThumbnailCacheOptions();
        options.CacheDirectory = ResolveCacheDirectory(options.CacheDirectory, configuration, contentRootPath);
        return options;
    }

    private static string ResolveCacheDirectory(
        string configuredCacheDirectory,
        IConfiguration configuration,
        string contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredCacheDirectory))
        {
            return RootPath(configuredCacheDirectory, contentRootPath);
        }

        var dataProtectionDirectory = configuration["DataProtection:KeyDirectory"];
        if (!string.IsNullOrWhiteSpace(dataProtectionDirectory))
        {
            var rootedKeyDirectory = RootPath(dataProtectionDirectory, contentRootPath);
            var persistentDataRoot = Directory.GetParent(rootedKeyDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(persistentDataRoot))
            {
                return Path.Combine(persistentDataRoot, "file-thumbnail-cache");
            }
        }

        return Path.Combine(contentRootPath, "data", "file-thumbnail-cache");
    }

    private static string RootPath(string path, string contentRootPath) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(contentRootPath, path));
}

public static class FileThumbnailFormatSupport
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".avif", ".ico",
        ".tif", ".tiff", ".heic", ".heif", ".jxl"
    };

    public static bool IsSupportedFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && SupportedExtensions.Contains(Path.GetExtension(fileName));
}
