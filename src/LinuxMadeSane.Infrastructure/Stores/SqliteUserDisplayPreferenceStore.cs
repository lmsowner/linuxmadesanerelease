// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteUserDisplayPreferenceStore(LinuxMadeSaneDbContext dbContext) : IUserDisplayPreferenceStore
{
    public async Task<UserDisplayPreference?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.UserDisplayPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(UserDisplayPreference preference, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.UserDisplayPreferences
            .SingleOrDefaultAsync(item => item.UserId == preference.UserId, cancellationToken);

        if (entity is null)
        {
            dbContext.UserDisplayPreferences.Add(Map(preference));
        }
        else
        {
            entity.ThemePaletteId = preference.ThemePaletteId;
            entity.ThemeMode = preference.ThemeMode;
            entity.FontScalePercent = preference.FontScalePercent;
            entity.TerminalCopyOnSelect = preference.TerminalCopyOnSelect;
            entity.DockerAiActionsApproved = preference.DockerAiActionsApproved;
            entity.UpdatedAtUtc = preference.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static UserDisplayPreference Map(UserDisplayPreferenceEntity entity) =>
        new(
            entity.UserId,
            entity.ThemePaletteId,
            entity.ThemeMode,
            entity.FontScalePercent,
            entity.TerminalCopyOnSelect,
            entity.DockerAiActionsApproved,
            entity.UpdatedAtUtc);

    private static UserDisplayPreferenceEntity Map(UserDisplayPreference preference) =>
        new()
        {
            UserId = preference.UserId,
            ThemePaletteId = preference.ThemePaletteId,
            ThemeMode = preference.ThemeMode,
            FontScalePercent = preference.FontScalePercent,
            TerminalCopyOnSelect = preference.TerminalCopyOnSelect,
            DockerAiActionsApproved = preference.DockerAiActionsApproved,
            UpdatedAtUtc = preference.UpdatedAtUtc
        };
}
