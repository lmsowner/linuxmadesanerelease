using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IFileBrowserShortcutStore
{
    Task<IReadOnlyList<FileBrowserShortcut>> ListAsync(
        Guid managedHostId,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut?> GetAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default);

    Task<FileBrowserShortcut?> FindByTargetPathAsync(
        Guid managedHostId,
        string targetPath,
        CancellationToken cancellationToken = default);

    Task NormalizeHostScopeAsync(
        Guid managedHostId,
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
