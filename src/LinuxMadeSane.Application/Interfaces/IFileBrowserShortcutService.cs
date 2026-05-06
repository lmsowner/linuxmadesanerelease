using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Interfaces;

public interface IFileBrowserShortcutService
{
    Task<IReadOnlyList<FileBrowserShortcut>> ListAsync(
        Guid managedHostId,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut> CreateAsync(
        Guid managedHostId,
        string label,
        string targetPath,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut> RenameAsync(
        Guid shortcutId,
        string label,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileBrowserShortcut>> MoveUpAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileBrowserShortcut>> MoveDownAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default);
}
