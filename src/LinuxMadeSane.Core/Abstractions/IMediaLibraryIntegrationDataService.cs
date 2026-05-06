using LinuxMadeSane.Core.Models.MediaLibrary;

namespace LinuxMadeSane.Core.Abstractions;

public interface IMediaLibraryIntegrationDataService
{
    Task<MediaLibraryIntegrationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(MediaLibraryIntegrationSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaLibraryRoot>> ListRootsAsync(CancellationToken cancellationToken = default);

    Task<MediaLibraryRoot?> GetRootAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveRootAsync(MediaLibraryRoot root, CancellationToken cancellationToken = default);

    Task DeleteRootAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaItem>> ListItemsAsync(MediaItemQuery query, CancellationToken cancellationToken = default);

    Task<MediaItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<MediaItem?> GetItemByFullPathAsync(string fullPath, CancellationToken cancellationToken = default);

    Task<MediaLibraryScanResult> ScanAsync(Guid? rootId, CancellationToken cancellationToken = default);

    Task<FfmpegToolStatus> GetFfmpegStatusAsync(CancellationToken cancellationToken = default);

    Task<MediaStreamOpenResult> OpenStreamAsync(Guid itemId, CancellationToken cancellationToken = default);
}
