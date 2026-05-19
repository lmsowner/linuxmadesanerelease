// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MediaLibrary;
using LinuxMadeSane.Core.Models.MediaLibrary;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMediaLibraryIntegrationService
{
    Task<MediaLibraryDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<MediaLibrarySettingsEditor> GetSettingsEditorAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(MediaLibrarySettingsEditor editor, CancellationToken cancellationToken = default);

    Task<MediaLibraryToolInstallResult> InstallFfmpegAsync(CancellationToken cancellationToken = default);

    Task<MediaLibraryRootEditor> GetRootEditorAsync(Guid? rootId, CancellationToken cancellationToken = default);

    Task<Guid> SaveRootAsync(MediaLibraryRootEditor editor, CancellationToken cancellationToken = default);

    Task DeleteRootAsync(Guid rootId, CancellationToken cancellationToken = default);

    Task QueueScanAsync(Guid? rootId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaItem>> ListItemsAsync(MediaItemQuery query, CancellationToken cancellationToken = default);

    Task<MediaItem?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<MediaFilePreviewViewModel?> GetPreviewForPathAsync(string fullPath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaItem>> ListPlaylistItemsAsync(MediaPlaylistRequest request, CancellationToken cancellationToken = default);

    string GeneratePlaylist(MediaPlaylistRequest request, IReadOnlyList<MediaItem> items, Func<MediaItem, string> streamUrlFactory);
}
