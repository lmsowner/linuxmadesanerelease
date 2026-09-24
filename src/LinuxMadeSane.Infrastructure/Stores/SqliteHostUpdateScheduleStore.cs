// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Updates;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteHostUpdateScheduleStore(LinuxMadeSaneDbContext dbContext) : IHostUpdateScheduleStore
{
    private const int SingletonId = 1;

    public async Task<HostUpdateScheduleSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.HostUpdateSchedules
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SingletonId, cancellationToken);
        return entity is null ? DefaultSettings() : Map(entity);
    }

    public async Task<HostUpdateScheduleSettings> SaveAsync(
        HostUpdateScheduleSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        var entity = await dbContext.HostUpdateSchedules
            .SingleOrDefaultAsync(item => item.Id == SingletonId, cancellationToken);
        if (entity is null)
        {
            entity = new HostUpdateScheduleEntity { Id = SingletonId };
            dbContext.HostUpdateSchedules.Add(entity);
        }

        entity.Enabled = normalized.Enabled;
        entity.HourLocal = normalized.HourLocal;
        entity.MinuteLocal = normalized.MinuteLocal;
        entity.ApplyPackageUpdates = normalized.ApplyPackageUpdates;
        entity.SecurityOnly = normalized.SecurityOnly;
        entity.UseDistUpgrade = normalized.UseDistUpgrade;
        entity.RebootIfRequired = normalized.RebootIfRequired;
        entity.LastRunAtUtc = normalized.LastRunAtUtc;
        entity.LastRunSummary = normalized.LastRunSummary;
        entity.LastRunDayKey = normalized.LastRunDayKey;
        entity.UpdatedAtUtc = normalized.UpdatedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    private static HostUpdateScheduleSettings DefaultSettings() =>
        new(
            Enabled: false,
            HourLocal: 3,
            MinuteLocal: 0,
            ApplyPackageUpdates: true,
            SecurityOnly: false,
            UseDistUpgrade: false,
            RebootIfRequired: false,
            LastRunAtUtc: null,
            LastRunSummary: null,
            LastRunDayKey: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private static HostUpdateScheduleSettings Normalize(HostUpdateScheduleSettings settings) =>
        settings with
        {
            HourLocal = Math.Clamp(settings.HourLocal, 0, 23),
            MinuteLocal = Math.Clamp(settings.MinuteLocal, 0, 59),
            LastRunSummary = string.IsNullOrWhiteSpace(settings.LastRunSummary)
                ? null
                : settings.LastRunSummary.Trim(),
            LastRunDayKey = string.IsNullOrWhiteSpace(settings.LastRunDayKey)
                ? null
                : settings.LastRunDayKey.Trim(),
            UpdatedAtUtc = settings.UpdatedAtUtc == default
                ? DateTimeOffset.UtcNow
                : settings.UpdatedAtUtc
        };

    private static HostUpdateScheduleSettings Map(HostUpdateScheduleEntity entity) =>
        new(
            entity.Enabled,
            entity.HourLocal,
            entity.MinuteLocal,
            entity.ApplyPackageUpdates,
            entity.SecurityOnly,
            entity.UseDistUpgrade,
            entity.RebootIfRequired,
            entity.LastRunAtUtc,
            entity.LastRunSummary,
            entity.LastRunDayKey,
            entity.UpdatedAtUtc);
}
