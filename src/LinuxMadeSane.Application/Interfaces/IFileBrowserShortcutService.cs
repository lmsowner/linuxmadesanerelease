// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Interfaces;

public interface IFileBrowserShortcutService
{
    Task<IReadOnlyList<FileBrowserShortcut>> ListAsync(Guid userId, Guid managedHostId,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut> CreateAsync(Guid userId, Guid managedHostId,
        string label,
        string targetPath,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut> RenameAsync(Guid userId, Guid shortcutId,
        string label,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileBrowserShortcut>> MoveUpAsync(Guid userId, Guid shortcutId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileBrowserShortcut>> MoveDownAsync(Guid userId, Guid shortcutId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid userId, Guid shortcutId,
        CancellationToken cancellationToken = default);
}
