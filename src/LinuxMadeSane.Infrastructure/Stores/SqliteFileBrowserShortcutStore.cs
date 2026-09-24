// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteFileBrowserShortcutStore(LinuxMadeSaneDbContext dbContext) : IFileBrowserShortcutStore
{
    private static readonly Guid HostScopedOwnerId = Guid.Empty;

    public async Task<IReadOnlyList<FileBrowserShortcut>> ListAsync(
        Guid managedHostId,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.FileBrowserShortcuts
            .AsNoTracking()
            .Where(item => item.UserId == HostScopedOwnerId && item.ManagedHostId == managedHostId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Label)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<FileBrowserShortcut?> GetAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.FileBrowserShortcuts
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == shortcutId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<FileBrowserShortcut?> FindByTargetPathAsync(
        Guid managedHostId,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.FileBrowserShortcuts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.UserId == HostScopedOwnerId &&
                        item.ManagedHostId == managedHostId &&
                        item.TargetPath == targetPath,
                cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task NormalizeHostScopeAsync(
        Guid managedHostId,
        CancellationToken cancellationToken = default)
    {
        var entities = (await dbContext.FileBrowserShortcuts
            .Where(item => item.ManagedHostId == managedHostId)
            .ToListAsync(cancellationToken))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .ToList();

        if (entities.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var seenTargetPaths = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<FileBrowserShortcutEntity>();
        var changed = false;
        var sortOrder = 0;

        foreach (var entity in entities)
        {
            if (!seenTargetPaths.Add(entity.TargetPath))
            {
                duplicates.Add(entity);
                changed = true;
                continue;
            }

            var entityChanged = false;
            if (entity.UserId != HostScopedOwnerId)
            {
                entity.UserId = HostScopedOwnerId;
                changed = true;
                entityChanged = true;
            }

            if (entity.SortOrder != sortOrder)
            {
                entity.SortOrder = sortOrder;
                changed = true;
                entityChanged = true;
            }

            if (entityChanged)
            {
                entity.UpdatedAtUtc = now;
            }

            sortOrder++;
        }

        if (duplicates.Count > 0)
        {
            dbContext.FileBrowserShortcuts.RemoveRange(duplicates);
        }

        if (!changed && duplicates.Count == 0)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(
        FileBrowserShortcut shortcut,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.FileBrowserShortcuts
            .SingleOrDefaultAsync(item => item.Id == shortcut.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.FileBrowserShortcuts.Add(Map(shortcut));
        }
        else
        {
            Apply(entity, shortcut);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveRangeAsync(
        IReadOnlyCollection<FileBrowserShortcut> shortcuts,
        CancellationToken cancellationToken = default)
    {
        if (shortcuts.Count == 0)
        {
            return;
        }

        var shortcutIds = shortcuts.Select(item => item.Id).ToArray();
        var existing = await dbContext.FileBrowserShortcuts
            .Where(item => shortcutIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var shortcut in shortcuts)
        {
            if (existing.TryGetValue(shortcut.Id, out var entity))
            {
                Apply(entity, shortcut);
            }
            else
            {
                dbContext.FileBrowserShortcuts.Add(Map(shortcut));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        Guid shortcutId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.FileBrowserShortcuts
            .SingleOrDefaultAsync(item => item.Id == shortcutId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.FileBrowserShortcuts.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FileBrowserShortcut Map(FileBrowserShortcutEntity entity) =>
        new(
            entity.Id,
            entity.UserId,
            entity.ManagedHostId,
            entity.Label,
            entity.TargetPath,
            entity.SortOrder,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static FileBrowserShortcutEntity Map(FileBrowserShortcut shortcut) =>
        new()
        {
            Id = shortcut.Id,
            UserId = shortcut.UserId,
            ManagedHostId = shortcut.ManagedHostId,
            Label = shortcut.Label,
            TargetPath = shortcut.TargetPath,
            SortOrder = shortcut.SortOrder,
            CreatedAtUtc = shortcut.CreatedAtUtc,
            UpdatedAtUtc = shortcut.UpdatedAtUtc
        };

    private static void Apply(FileBrowserShortcutEntity entity, FileBrowserShortcut shortcut)
    {
        entity.UserId = shortcut.UserId;
        entity.ManagedHostId = shortcut.ManagedHostId;
        entity.Label = shortcut.Label;
        entity.TargetPath = shortcut.TargetPath;
        entity.SortOrder = shortcut.SortOrder;
        entity.CreatedAtUtc = shortcut.CreatedAtUtc;
        entity.UpdatedAtUtc = shortcut.UpdatedAtUtc;
    }
}
