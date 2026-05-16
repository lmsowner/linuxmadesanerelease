// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MediaLibrary;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteMediaLibraryIntegrationDataService(
    LinuxMadeSaneDbContext dbContext,
    ILinuxCommandRunner commandRunner,
    ILogger<SqliteMediaLibraryIntegrationDataService> logger) : IMediaLibraryIntegrationDataService
{
    private const int SettingsId = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".ts", ".webm", ".flv", ".wmv"
    };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".m4a", ".flac", ".wav", ".ogg", ".opus"
    };
    private static readonly HashSet<string> PlaylistExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8"
    };
    private static readonly HashSet<string> BrowserAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".m4a", ".wav", ".ogg", ".opus"
    };
    private static readonly HashSet<string> BrowserVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm"
    };

    public async Task<MediaLibraryIntegrationSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MediaLibrarySettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SettingsId, cancellationToken);

        if (entity is not null)
        {
            return Map(entity);
        }

        var now = DateTimeOffset.UtcNow;
        var settings = MediaLibraryIntegrationSettings.CreateDefault(now);
        dbContext.MediaLibrarySettings.Add(Map(settings));
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task SaveSettingsAsync(MediaLibraryIntegrationSettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MediaLibrarySettings
            .SingleOrDefaultAsync(item => item.Id == SettingsId, cancellationToken);

        if (entity is null)
        {
            dbContext.MediaLibrarySettings.Add(Map(settings));
        }
        else
        {
            Apply(entity, settings);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaLibraryRoot>> ListRootsAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.MediaLibraryRoots
            .AsNoTracking()
            .OrderBy(root => root.SortOrder)
            .ThenBy(root => root.Name)
            .ToArrayAsync(cancellationToken))
        .Select(Map)
        .ToArray();

    public async Task<MediaLibraryRoot?> GetRootAsync(Guid id, CancellationToken cancellationToken = default) =>
        MapOrNull(await dbContext.MediaLibraryRoots
            .AsNoTracking()
            .SingleOrDefaultAsync(root => root.Id == id, cancellationToken));

    public async Task SaveRootAsync(MediaLibraryRoot root, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MediaLibraryRoots
            .SingleOrDefaultAsync(item => item.Id == root.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.MediaLibraryRoots.Add(Map(root));
        }
        else
        {
            Apply(entity, root);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRootAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MediaLibraryRoots
            .SingleOrDefaultAsync(root => root.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.MediaLibraryRoots.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaItem>> ListItemsAsync(MediaItemQuery query, CancellationToken cancellationToken = default)
    {
        var items = dbContext.MediaItems
            .AsNoTracking()
            .Include(item => item.LibraryRoot)
            .AsQueryable();

        if (query.RootId.HasValue)
        {
            items = items.Where(item => item.LibraryRootId == query.RootId.Value);
        }

        if (query.Category.HasValue)
        {
            var category = (int)query.Category.Value;
            items = items.Where(item => item.LibraryRoot != null && item.LibraryRoot.Category == category);
        }

        if (query.MediaKind.HasValue)
        {
            var mediaKind = (int)query.MediaKind.Value;
            items = items.Where(item => item.MediaKind == mediaKind);
        }
        else if (!query.IncludeUnsupported)
        {
            items = items.Where(item => item.MediaKind == (int)MediaKind.Video || item.MediaKind == (int)MediaKind.Audio || item.MediaKind == (int)MediaKind.Playlist);
        }

        if (!string.IsNullOrWhiteSpace(query.Extension))
        {
            var extension = NormalizeExtension(query.Extension);
            items = items.Where(item => item.Extension == extension);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            items = items.Where(item => item.FileName.Contains(search) || item.RelativePath.Contains(search));
        }

        items = query.SortMode switch
        {
            MediaSortMode.Path => items.OrderBy(item => item.RelativePath),
            MediaSortMode.LastModifiedDescending => items.OrderByDescending(item => item.LastModifiedUtc).ThenBy(item => item.FileName),
            MediaSortMode.LastModifiedAscending => items.OrderBy(item => item.LastModifiedUtc).ThenBy(item => item.FileName),
            MediaSortMode.SizeDescending => items.OrderByDescending(item => item.SizeBytes).ThenBy(item => item.FileName),
            _ => items.OrderBy(item => item.FileName).ThenBy(item => item.RelativePath)
        };

        var take = query.MaxItems <= 0 ? 500 : query.MaxItems;
        return (await items
            .Take(take)
            .ToArrayAsync(cancellationToken))
            .Select(Map)
            .ToArray();
    }

    public async Task<MediaItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        MapOrNull(await dbContext.MediaItems
            .AsNoTracking()
            .Include(item => item.LibraryRoot)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken));

    public async Task<MediaItem?> GetItemByFullPathAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(fullPath);
        return MapOrNull(await dbContext.MediaItems
            .AsNoTracking()
            .Include(item => item.LibraryRoot)
            .SingleOrDefaultAsync(item => item.FullPath == normalizedPath, cancellationToken));
    }

    public async Task<MediaLibraryScanResult> ScanAsync(Guid? rootId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var query = dbContext.MediaLibraryRoots.AsQueryable();
        query = rootId.HasValue
            ? query.Where(root => root.Id == rootId.Value)
            : query.Where(root => root.Enabled);

        var roots = await query
            .OrderBy(root => root.SortOrder)
            .ThenBy(root => root.Name)
            .ToArrayAsync(cancellationToken);

        if (roots.Length == 0)
        {
            return new MediaLibraryScanResult(
                rootId,
                MediaScanStatus.NotScanned,
                0,
                0,
                0,
                0,
                0,
                rootId.HasValue ? "The selected media folder no longer exists." : "No enabled media folders are configured.",
                DateTimeOffset.UtcNow);
        }

        var aggregate = new ScanAggregate();
        MediaScanStatus finalStatus = MediaScanStatus.Completed;
        var messages = new List<string>();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ScanRootAsync(root, settings, cancellationToken);
            aggregate.RootsScanned++;
            aggregate.ItemsIndexed += result.ItemsIndexed;
            aggregate.ItemsUpdated += result.ItemsUpdated;
            aggregate.ItemsRemoved += result.ItemsRemoved;
            aggregate.ItemsSkipped += result.ItemsSkipped;
            messages.Add($"{root.Name}: {result.Message}");

            if (result.Status != MediaScanStatus.Completed)
            {
                finalStatus = result.Status;
            }
        }

        return new MediaLibraryScanResult(
            rootId,
            finalStatus,
            aggregate.RootsScanned,
            aggregate.ItemsIndexed,
            aggregate.ItemsUpdated,
            aggregate.ItemsRemoved,
            aggregate.ItemsSkipped,
            string.Join(" ", messages),
            DateTimeOffset.UtcNow);
    }

    public async Task<FfmpegToolStatus> GetFfmpegStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var ffmpeg = await ProbeToolAsync(settings.FfmpegPath, "ffmpeg", cancellationToken);
        var ffprobe = await ProbeToolAsync(settings.FfprobePath, "ffprobe", cancellationToken);
        var message = ffmpeg.Available
            ? $"FFmpeg available: {ffmpeg.Version}"
            : "FFmpeg was not found. Playlist generation and direct streaming still work without it.";

        return new FfmpegToolStatus(
            ffmpeg.Available,
            ffmpeg.Path,
            ffmpeg.Version,
            ffprobe.Available,
            ffprobe.Path,
            ffprobe.Version,
            message);
    }

    public async Task<MediaStreamOpenResult> OpenStreamAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MediaItems
            .AsNoTracking()
            .Include(item => item.LibraryRoot)
            .SingleOrDefaultAsync(item => item.Id == itemId, cancellationToken);

        if (entity is null || entity.LibraryRoot is null)
        {
            throw new FileNotFoundException("Media item was not found.");
        }

        var fullPath = Path.GetFullPath(entity.FullPath);
        var rootPath = Path.GetFullPath(entity.LibraryRoot.Path);
        if (!IsPathInsideRoot(fullPath, rootPath))
        {
            throw new InvalidOperationException("The indexed media path is no longer inside its configured library root.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The indexed media file no longer exists.");
        }

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 128, useAsync: true);
        return new MediaStreamOpenResult(
            Map(entity),
            stream,
            string.IsNullOrWhiteSpace(entity.MimeType) ? "application/octet-stream" : entity.MimeType,
            fileInfo.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private async Task<MediaLibraryScanResult> ScanRootAsync(
        MediaLibraryRootEntity root,
        MediaLibraryIntegrationSettings settings,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        root.LastScanStatus = (int)MediaScanStatus.Running;
        root.LastScanMessage = "Scanning...";
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var rootPath = Path.GetFullPath(root.Path);
            var directory = new DirectoryInfo(rootPath);
            if (!directory.Exists)
            {
                return await CompleteRootScanAsync(root, MediaScanStatus.FolderMissing, 0, 0, 0, 0, "Folder missing.", cancellationToken);
            }

            var existingItems = await dbContext.MediaItems
                .Where(item => item.LibraryRootId == root.Id)
                .ToDictionaryAsync(item => item.RelativePath, StringComparer.Ordinal, cancellationToken);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var includeExtensions = DeserializeList(root.IncludeExtensionsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excludeExtensions = DeserializeList(root.ExcludeExtensionsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excludeFolders = DeserializeList(root.ExcludeFoldersJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var aggregate = new ScanAggregate();

            foreach (var file in EnumerateFiles(directory, root.Recursive, excludeFolders, cancellationToken, () => aggregate.ItemsSkipped++))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = NormalizeExtension(file.Extension);
                var mediaKind = ResolveMediaKind(extension);

                if (!ShouldIndex(extension, mediaKind, includeExtensions, excludeExtensions, settings.ShowUnsupportedFiles))
                {
                    aggregate.ItemsSkipped++;
                    continue;
                }

                var relativePath = Path.GetRelativePath(rootPath, file.FullName);
                seen.Add(relativePath);
                DateTimeOffset? lastModifiedUtc = file.LastWriteTimeUtc == DateTime.MinValue
                    ? null
                    : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

                if (existingItems.TryGetValue(relativePath, out var existing))
                {
                    Apply(existing, root, file, relativePath, extension, mediaKind, lastModifiedUtc, DateTimeOffset.UtcNow);
                    aggregate.ItemsUpdated++;
                }
                else
                {
                    dbContext.MediaItems.Add(CreateItem(root, file, relativePath, extension, mediaKind, lastModifiedUtc, DateTimeOffset.UtcNow));
                    aggregate.ItemsIndexed++;
                }
            }

            var removedItems = existingItems
                .Where(item => !seen.Contains(item.Key))
                .Select(item => item.Value)
                .ToArray();

            if (removedItems.Length > 0)
            {
                dbContext.MediaItems.RemoveRange(removedItems);
                aggregate.ItemsRemoved = removedItems.Length;
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var message = $"Indexed {aggregate.ItemsIndexed} new, updated {aggregate.ItemsUpdated}, removed {aggregate.ItemsRemoved}, skipped {aggregate.ItemsSkipped} in {elapsed.TotalSeconds:0.0}s.";
            return await CompleteRootScanAsync(root, MediaScanStatus.Completed, aggregate.ItemsIndexed, aggregate.ItemsUpdated, aggregate.ItemsRemoved, aggregate.ItemsSkipped, message, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Media library scan permission issue for root {RootId}", root.Id);
            return await CompleteRootScanAsync(root, MediaScanStatus.PermissionIssue, 0, 0, 0, 0, exception.Message, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or System.Security.SecurityException)
        {
            logger.LogWarning(exception, "Media library scan failed for root {RootId}", root.Id);
            return await CompleteRootScanAsync(root, MediaScanStatus.Failed, 0, 0, 0, 0, exception.Message, cancellationToken);
        }
    }

    private async Task<MediaLibraryScanResult> CompleteRootScanAsync(
        MediaLibraryRootEntity root,
        MediaScanStatus status,
        int indexed,
        int updated,
        int removed,
        int skipped,
        string message,
        CancellationToken cancellationToken)
    {
        root.LastScanStatus = (int)status;
        root.LastScanUtc = DateTimeOffset.UtcNow;
        root.LastScanMessage = message;
        root.UpdatedUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MediaLibraryScanResult(root.Id, status, 1, indexed, updated, removed, skipped, message, root.LastScanUtc.Value);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(
        DirectoryInfo root,
        bool recursive,
        HashSet<string> excludeFolders,
        CancellationToken cancellationToken,
        Action skipped)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var rootPath = root.FullName;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileInfo[] files;
            DirectoryInfo[] directories;

            try
            {
                files = directory.GetFiles();
                directories = recursive ? directory.GetDirectories() : [];
            }
            catch (UnauthorizedAccessException)
            {
                skipped();
                if (string.Equals(directory.FullName, rootPath, StringComparison.Ordinal))
                {
                    throw;
                }

                continue;
            }
            catch (IOException)
            {
                skipped();
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in directories)
            {
                if (ShouldExcludeDirectory(rootPath, child, excludeFolders))
                {
                    skipped();
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static bool ShouldExcludeDirectory(string rootPath, DirectoryInfo directory, HashSet<string> excludeFolders)
    {
        if (excludeFolders.Count == 0)
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(rootPath, directory.FullName).Replace('\\', '/');
        return excludeFolders.Contains(directory.Name) || excludeFolders.Contains(relativePath);
    }

    private static bool ShouldIndex(
        string extension,
        MediaKind mediaKind,
        HashSet<string> includeExtensions,
        HashSet<string> excludeExtensions,
        bool showUnsupportedFiles)
    {
        if (string.IsNullOrWhiteSpace(extension) || excludeExtensions.Contains(extension))
        {
            return false;
        }

        if (includeExtensions.Count > 0 && !includeExtensions.Contains(extension))
        {
            return false;
        }

        return mediaKind != MediaKind.Other || showUnsupportedFiles;
    }

    private static MediaItemEntity CreateItem(
        MediaLibraryRootEntity root,
        FileInfo file,
        string relativePath,
        string extension,
        MediaKind mediaKind,
        DateTimeOffset? lastModifiedUtc,
        DateTimeOffset indexedUtc)
    {
        var entity = new MediaItemEntity { Id = Guid.NewGuid(), LibraryRootId = root.Id };
        Apply(entity, root, file, relativePath, extension, mediaKind, lastModifiedUtc, indexedUtc);
        return entity;
    }

    private static void Apply(
        MediaItemEntity entity,
        MediaLibraryRootEntity root,
        FileInfo file,
        string relativePath,
        string extension,
        MediaKind mediaKind,
        DateTimeOffset? lastModifiedUtc,
        DateTimeOffset indexedUtc)
    {
        entity.LibraryRootId = root.Id;
        entity.RelativePath = relativePath;
        entity.FullPath = Path.GetFullPath(file.FullName);
        entity.FileName = file.Name;
        entity.Extension = extension;
        entity.MediaKind = (int)mediaKind;
        entity.SizeBytes = file.Length;
        entity.LastModifiedUtc = lastModifiedUtc;
        entity.MimeType = ResolveMimeType(extension, mediaKind);
        entity.IsBrowserCompatible = ResolveBrowserCompatibility(extension, mediaKind);
        entity.IsVlcCompatible = mediaKind is MediaKind.Video or MediaKind.Audio or MediaKind.Playlist;
        entity.RequiresRemux = mediaKind == MediaKind.Video && RequiresLikelyRemux(extension);
        entity.RequiresTranscode = null;
        entity.IndexedUtc = indexedUtc;
    }

    private async Task<(bool Available, string Path, string Version)> ProbeToolAsync(
        string configuredPath,
        string fallbackCommand,
        CancellationToken cancellationToken)
    {
        var command = string.IsNullOrWhiteSpace(configuredPath) ? fallbackCommand : configuredPath.Trim();
        try
        {
            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    command,
                    ["-version"],
                    false,
                    TimeSpan.FromSeconds(10),
                    $"Inspect {fallbackCommand}")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);

            if (result.ExitCode != 0)
            {
                return (false, command, string.Empty);
            }

            var version = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "available";
            return (true, command, version);
        }
        catch
        {
            return (false, command, string.Empty);
        }
    }

    private static MediaLibraryIntegrationSettings Map(MediaLibrarySettingsEntity entity) =>
        new(
            entity.IsEnabled,
            (MediaStreamingMode)entity.StreamingMode,
            entity.FfmpegPath,
            entity.FfprobePath,
            entity.EnableMetadataProbing,
            entity.EnableRemux,
            entity.EnableTranscoding,
            entity.MaxConcurrentFfmpegJobs,
            entity.TempCacheFolder,
            entity.CacheExpiryMinutes,
            entity.MaxCacheSizeBytes,
            (MediaHardwareAcceleration)entity.HardwareAcceleration,
            entity.RequireLoginForPlaylists,
            entity.RequireLoginForStreams,
            entity.AllowLanAnonymousAccess,
            entity.GenerateTemporarySignedStreamUrls,
            entity.SignedUrlExpiryMinutes,
            entity.IpAllowlistCsv,
            entity.ShowUnsupportedFiles,
            entity.AllowUnknownBrowserPlayback,
            entity.CreatedUtc,
            entity.UpdatedUtc);

    private static MediaLibrarySettingsEntity Map(MediaLibraryIntegrationSettings settings)
    {
        var entity = new MediaLibrarySettingsEntity { Id = SettingsId };
        Apply(entity, settings);
        return entity;
    }

    private static void Apply(MediaLibrarySettingsEntity entity, MediaLibraryIntegrationSettings settings)
    {
        entity.IsEnabled = settings.IsEnabled;
        entity.StreamingMode = (int)settings.StreamingMode;
        entity.FfmpegPath = settings.FfmpegPath;
        entity.FfprobePath = settings.FfprobePath;
        entity.EnableMetadataProbing = settings.EnableMetadataProbing;
        entity.EnableRemux = settings.EnableRemux;
        entity.EnableTranscoding = settings.EnableTranscoding;
        entity.MaxConcurrentFfmpegJobs = settings.MaxConcurrentFfmpegJobs;
        entity.TempCacheFolder = settings.TempCacheFolder;
        entity.CacheExpiryMinutes = settings.CacheExpiryMinutes;
        entity.MaxCacheSizeBytes = settings.MaxCacheSizeBytes;
        entity.HardwareAcceleration = (int)settings.HardwareAcceleration;
        entity.RequireLoginForPlaylists = settings.RequireLoginForPlaylists;
        entity.RequireLoginForStreams = settings.RequireLoginForStreams;
        entity.AllowLanAnonymousAccess = settings.AllowLanAnonymousAccess;
        entity.GenerateTemporarySignedStreamUrls = settings.GenerateTemporarySignedStreamUrls;
        entity.SignedUrlExpiryMinutes = settings.SignedUrlExpiryMinutes;
        entity.IpAllowlistCsv = settings.IpAllowlistCsv;
        entity.ShowUnsupportedFiles = settings.ShowUnsupportedFiles;
        entity.AllowUnknownBrowserPlayback = settings.AllowUnknownBrowserPlayback;
        entity.CreatedUtc = settings.CreatedUtc;
        entity.UpdatedUtc = settings.UpdatedUtc;
    }

    private static MediaLibraryRootEntity Map(MediaLibraryRoot root) =>
        new()
        {
            Id = root.Id,
            Name = root.Name,
            Path = root.Path,
            Category = (int)root.Category,
            CustomCategoryName = root.CustomCategoryName,
            Enabled = root.Enabled,
            Recursive = root.Recursive,
            IncludeExtensionsJson = SerializeList(root.IncludeExtensions),
            ExcludeExtensionsJson = SerializeList(root.ExcludeExtensions),
            ExcludeFoldersJson = SerializeList(root.ExcludeFolders),
            SortOrder = root.SortOrder,
            Notes = root.Notes,
            CreatedUtc = root.CreatedUtc,
            UpdatedUtc = root.UpdatedUtc,
            LastScanUtc = root.LastScanUtc,
            LastScanStatus = (int)root.LastScanStatus,
            LastScanMessage = root.LastScanMessage
        };

    private static void Apply(MediaLibraryRootEntity entity, MediaLibraryRoot root)
    {
        entity.Name = root.Name;
        entity.Path = root.Path;
        entity.Category = (int)root.Category;
        entity.CustomCategoryName = root.CustomCategoryName;
        entity.Enabled = root.Enabled;
        entity.Recursive = root.Recursive;
        entity.IncludeExtensionsJson = SerializeList(root.IncludeExtensions);
        entity.ExcludeExtensionsJson = SerializeList(root.ExcludeExtensions);
        entity.ExcludeFoldersJson = SerializeList(root.ExcludeFolders);
        entity.SortOrder = root.SortOrder;
        entity.Notes = root.Notes;
        entity.CreatedUtc = root.CreatedUtc;
        entity.UpdatedUtc = root.UpdatedUtc;
        entity.LastScanUtc = root.LastScanUtc;
        entity.LastScanStatus = (int)root.LastScanStatus;
        entity.LastScanMessage = root.LastScanMessage;
    }

    private static MediaLibraryRoot? MapOrNull(MediaLibraryRootEntity? entity) =>
        entity is null ? null : Map(entity);

    private static MediaLibraryRoot Map(MediaLibraryRootEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.Path,
            (MediaLibraryCategory)entity.Category,
            entity.CustomCategoryName,
            entity.Enabled,
            entity.Recursive,
            DeserializeList(entity.IncludeExtensionsJson),
            DeserializeList(entity.ExcludeExtensionsJson),
            DeserializeList(entity.ExcludeFoldersJson),
            entity.SortOrder,
            entity.Notes,
            entity.CreatedUtc,
            entity.UpdatedUtc,
            entity.LastScanUtc,
            (MediaScanStatus)entity.LastScanStatus,
            entity.LastScanMessage);

    private static MediaItem? MapOrNull(MediaItemEntity? entity) =>
        entity is null ? null : Map(entity);

    private static MediaItem Map(MediaItemEntity entity) =>
        new(
            entity.Id,
            entity.LibraryRootId,
            entity.LibraryRoot?.Name ?? string.Empty,
            entity.LibraryRoot is null ? MediaLibraryCategory.Mixed : (MediaLibraryCategory)entity.LibraryRoot.Category,
            entity.RelativePath,
            entity.FullPath,
            entity.FileName,
            entity.Extension,
            (MediaKind)entity.MediaKind,
            entity.SizeBytes,
            entity.LastModifiedUtc,
            entity.MimeType,
            entity.DurationSeconds,
            entity.VideoCodec,
            entity.AudioCodec,
            entity.Container,
            entity.IsBrowserCompatible,
            entity.IsVlcCompatible,
            entity.RequiresRemux,
            entity.RequiresTranscode,
            entity.IndexedUtc);

    private static IReadOnlyList<string> DeserializeList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string SerializeList(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static MediaKind ResolveMediaKind(string extension)
    {
        if (VideoExtensions.Contains(extension))
        {
            return MediaKind.Video;
        }

        if (AudioExtensions.Contains(extension))
        {
            return MediaKind.Audio;
        }

        return PlaylistExtensions.Contains(extension) ? MediaKind.Playlist : MediaKind.Other;
    }

    private static string ResolveMimeType(string extension, MediaKind mediaKind) =>
        extension.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".ts" => "video/mp2t",
            ".webm" => "video/webm",
            ".flv" => "video/x-flv",
            ".wmv" => "video/x-ms-wmv",
            ".mp3" => "audio/mpeg",
            ".aac" => "audio/aac",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".m3u" => "audio/x-mpegurl",
            ".m3u8" => "application/vnd.apple.mpegurl",
            _ when mediaKind == MediaKind.Video => "application/octet-stream",
            _ when mediaKind == MediaKind.Audio => "application/octet-stream",
            _ => "application/octet-stream"
        };

    private static bool? ResolveBrowserCompatibility(string extension, MediaKind mediaKind)
    {
        if (mediaKind == MediaKind.Audio && BrowserAudioExtensions.Contains(extension))
        {
            return true;
        }

        if (mediaKind == MediaKind.Video && BrowserVideoExtensions.Contains(extension))
        {
            return true;
        }

        return mediaKind == MediaKind.Playlist ? false : null;
    }

    private static bool RequiresLikelyRemux(string extension) =>
        extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".flv", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtension(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return (trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : $".{trimmed}").ToLowerInvariant();
    }

    private static bool IsPathInsideRoot(string fullPath, string rootPath)
    {
        var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
               string.Equals(fullPath, rootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed class ScanAggregate
    {
        public int RootsScanned { get; set; }
        public int ItemsIndexed { get; set; }
        public int ItemsUpdated { get; set; }
        public int ItemsRemoved { get; set; }
        public int ItemsSkipped { get; set; }
    }
}
