// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Services;

public sealed class FileBrowserShortcutService(IFileBrowserShortcutStore store) : IFileBrowserShortcutService
{
    private static readonly Guid HostScopedOwnerId = Guid.Empty;

    public Task<IReadOnlyList<FileBrowserShortcut>> ListAsync(
        Guid managedHostId,
        CancellationToken cancellationToken = default) =>
        ListHostScopedAsync(managedHostId, cancellationToken);

    public async Task<FileBrowserShortcut> CreateAsync(
        Guid managedHostId,
        string label,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        await EnsureHostScopedShortcutsAsync(managedHostId, cancellationToken);

        var normalizedTargetPath = NormalizeTargetPath(targetPath);
        var existing = await store.FindByTargetPathAsync(managedHostId, normalizedTargetPath, cancellationToken);
        if (existing is not null)
        {
            var normalizedExistingLabel = NormalizeLabel(label, existing.TargetPath);
            if (string.Equals(existing.Label, normalizedExistingLabel, StringComparison.Ordinal))
            {
                return existing;
            }

            var updatedExisting = existing with
            {
                Label = normalizedExistingLabel,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await store.SaveAsync(updatedExisting, cancellationToken);
            return updatedExisting;
        }

        var current = await store.ListAsync(managedHostId, cancellationToken);
        var shortcut = new FileBrowserShortcut(
            Guid.NewGuid(),
            HostScopedOwnerId,
            managedHostId,
            NormalizeLabel(label, normalizedTargetPath),
            normalizedTargetPath,
            current.Count,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        await store.SaveAsync(shortcut, cancellationToken);
        return shortcut;
    }

    public async Task<FileBrowserShortcut> RenameAsync(
        Guid shortcutId,
        string label,
        CancellationToken cancellationToken = default)
    {
        var shortcut = await RequireHostScopedShortcutAsync(shortcutId, cancellationToken);
        var updated = shortcut with
        {
            Label = NormalizeLabel(label, shortcut.TargetPath),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public Task<IReadOnlyList<FileBrowserShortcut>> MoveUpAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default) =>
        MoveAsync(shortcutId, -1, cancellationToken);

    public Task<IReadOnlyList<FileBrowserShortcut>> MoveDownAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default) =>
        MoveAsync(shortcutId, 1, cancellationToken);

    public async Task DeleteAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default)
    {
        var shortcut = await RequireHostScopedShortcutAsync(shortcutId, cancellationToken);
        await store.DeleteAsync(shortcut.Id, cancellationToken);

        var remaining = await store.ListAsync(shortcut.ManagedHostId, cancellationToken);
        await SaveNormalizedOrderingAsync(remaining, cancellationToken);
    }

    private async Task<IReadOnlyList<FileBrowserShortcut>> MoveAsync(
        Guid shortcutId,
        int direction,
        CancellationToken cancellationToken)
    {
        var shortcut = await RequireHostScopedShortcutAsync(shortcutId, cancellationToken);
        var shortcuts = (await store.ListAsync(shortcut.ManagedHostId, cancellationToken)).ToList();
        var index = shortcuts.FindIndex(item => item.Id == shortcutId);
        if (index < 0)
        {
            throw new InvalidOperationException("Shortcut not found.");
        }

        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= shortcuts.Count)
        {
            return shortcuts;
        }

        (shortcuts[index], shortcuts[targetIndex]) = (shortcuts[targetIndex], shortcuts[index]);
        await SaveNormalizedOrderingAsync(shortcuts, cancellationToken);
        return shortcuts
            .Select((item, order) => item with { SortOrder = order })
            .ToArray();
    }

    private async Task<FileBrowserShortcut> RequireHostScopedShortcutAsync(
        Guid shortcutId,
        CancellationToken cancellationToken)
    {
        var shortcut = await store.GetAsync(shortcutId, cancellationToken);
        if (shortcut is null)
        {
            throw new InvalidOperationException("Shortcut not found.");
        }

        if (shortcut.UserId == HostScopedOwnerId)
        {
            return shortcut;
        }

        await EnsureHostScopedShortcutsAsync(shortcut.ManagedHostId, cancellationToken);

        shortcut = await store.GetAsync(shortcutId, cancellationToken);
        if (shortcut is null || shortcut.UserId != HostScopedOwnerId)
        {
            throw new InvalidOperationException("Shortcut not found.");
        }

        return shortcut;
    }

    private async Task<IReadOnlyList<FileBrowserShortcut>> ListHostScopedAsync(
        Guid managedHostId,
        CancellationToken cancellationToken)
    {
        await EnsureHostScopedShortcutsAsync(managedHostId, cancellationToken);
        return await store.ListAsync(managedHostId, cancellationToken);
    }

    private Task EnsureHostScopedShortcutsAsync(
        Guid managedHostId,
        CancellationToken cancellationToken) =>
        store.NormalizeHostScopeAsync(managedHostId, cancellationToken);

    private async Task SaveNormalizedOrderingAsync(
        IReadOnlyList<FileBrowserShortcut> shortcuts,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalized = shortcuts
            .Select((item, index) => item with
            {
                SortOrder = index,
                UpdatedAtUtc = now
            })
            .ToArray();

        await store.SaveRangeAsync(normalized, cancellationToken);
    }

    private static string NormalizeTargetPath(string targetPath)
    {
        var normalized = (targetPath ?? string.Empty).Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Choose a folder before creating a shortcut.");
        }

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized.Length > 1
            ? normalized.TrimEnd('/')
            : normalized;
    }

    private static string NormalizeLabel(string label, string targetPath)
    {
        var normalized = (label ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var lastSeparator = targetPath.LastIndexOf('/');
        if (lastSeparator >= 0 && lastSeparator < targetPath.Length - 1)
        {
            return targetPath[(lastSeparator + 1)..];
        }

        return targetPath == "/" ? "/" : "Shortcut";
    }
}
