// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IFileBrowserShortcutStore
{
    Task<IReadOnlyList<FileBrowserShortcut>> ListAsync(Guid userId, Guid managedHostId,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut?> GetAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut?> FindByTargetPathAsync(Guid userId, Guid managedHostId,
        string targetPath,
        CancellationToken cancellationToken = default);

    Task NormalizeHostScopeAsync(Guid userId, Guid managedHostId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        FileBrowserShortcut shortcut,
        CancellationToken cancellationToken = default);

    Task SaveRangeAsync(
        IReadOnlyCollection<FileBrowserShortcut> shortcuts,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default);
}
