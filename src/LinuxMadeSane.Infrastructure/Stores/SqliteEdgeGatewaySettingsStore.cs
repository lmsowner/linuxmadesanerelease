// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.EdgeGateway;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteEdgeGatewaySettingsStore(LinuxMadeSaneDbContext dbContext) : IEdgeGatewaySettingsStore
{
    private const int SettingsRowId = 1;

    public async Task<EdgeGatewaySettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EdgeGatewaySettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == SettingsRowId, cancellationToken);
        if (entity is not null)
        {
            return Map(entity);
        }

        var now = DateTimeOffset.UtcNow;
        return new EdgeGatewaySettings(SettingsRowId, "relay", now, now);
    }

    public async Task SaveAsync(EdgeGatewaySettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.EdgeGatewaySettings
            .SingleOrDefaultAsync(item => item.Id == SettingsRowId, cancellationToken);
        if (entity is null)
        {
            dbContext.EdgeGatewaySettings.Add(new EdgeGatewaySettingsEntity
            {
                Id = SettingsRowId,
                GatewaySubdomain = settings.GatewaySubdomain,
                CreatedAtUtc = settings.CreatedAtUtc,
                UpdatedAtUtc = settings.UpdatedAtUtc
            });
        }
        else
        {
            entity.GatewaySubdomain = settings.GatewaySubdomain;
            entity.UpdatedAtUtc = settings.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static EdgeGatewaySettings Map(EdgeGatewaySettingsEntity entity) =>
        new(entity.Id, entity.GatewaySubdomain, entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
