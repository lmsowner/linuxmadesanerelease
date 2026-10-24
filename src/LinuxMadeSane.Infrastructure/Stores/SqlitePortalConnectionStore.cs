// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions.Portal;
using LinuxMadeSane.Core.Models.Portal;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqlitePortalConnectionStore(LinuxMadeSaneDbContext dbContext) : IPortalConnectionStore
{
    public async Task<PortalConnectionSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PortalConnectionSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(PortalConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PortalConnectionSettings
            .SingleOrDefaultAsync(item => item.Id == settings.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.PortalConnectionSettings.Add(Map(settings));
        }
        else
        {
            entity.LocalInstanceId = settings.LocalInstanceId;
            entity.PortalBaseUrl = settings.PortalBaseUrl;
            entity.InstanceDisplayName = settings.InstanceDisplayName;
            entity.IsEnabled = settings.IsEnabled;
            entity.InstanceIdentityPrivateKeySecretReference = settings.InstanceIdentityPrivateKeySecretReference;
            entity.InstanceIdentityPublicKey = settings.InstanceIdentityPublicKey;
            entity.InstanceIdentityPublicKeyFingerprint = settings.InstanceIdentityPublicKeyFingerprint;
            entity.PairingCode = settings.PairingCode;
            entity.PairingCodeGeneratedAtUtc = settings.PairingCodeGeneratedAtUtc;
            entity.PairingCodeExpiresAtUtc = settings.PairingCodeExpiresAtUtc;
            entity.PortalOrganizationId = settings.PortalOrganizationId;
            entity.PortalOrganizationName = settings.PortalOrganizationName;
            entity.PortalInstanceId = settings.PortalInstanceId;
            entity.PortalApiKeyId = settings.PortalApiKeyId;
            entity.PortalApiSecretReference = settings.PortalApiSecretReference;
            entity.LastConnectionStatus = settings.LastConnectionStatus;
            entity.LastConnectedAtUtc = settings.LastConnectedAtUtc;
            entity.LastHeartbeatAtUtc = settings.LastHeartbeatAtUtc;
            entity.CreatedAtUtc = settings.CreatedAtUtc;
            entity.UpdatedAtUtc = settings.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static PortalConnectionSettings Map(PortalConnectionSettingsEntity entity) =>
        new(
            entity.Id,
            entity.LocalInstanceId,
            entity.PortalBaseUrl,
            entity.InstanceDisplayName,
            entity.IsEnabled,
            entity.InstanceIdentityPrivateKeySecretReference,
            entity.InstanceIdentityPublicKey,
            entity.InstanceIdentityPublicKeyFingerprint,
            entity.PairingCode,
            entity.PairingCodeGeneratedAtUtc,
            entity.PairingCodeExpiresAtUtc,
            entity.PortalOrganizationId,
            entity.PortalOrganizationName,
            entity.PortalInstanceId,
            entity.PortalApiKeyId,
            entity.PortalApiSecretReference,
            entity.LastConnectionStatus,
            entity.LastConnectedAtUtc,
            entity.LastHeartbeatAtUtc,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static PortalConnectionSettingsEntity Map(PortalConnectionSettings settings) =>
        new()
        {
            Id = settings.Id,
            LocalInstanceId = settings.LocalInstanceId,
            PortalBaseUrl = settings.PortalBaseUrl,
            InstanceDisplayName = settings.InstanceDisplayName,
            IsEnabled = settings.IsEnabled,
            InstanceIdentityPrivateKeySecretReference = settings.InstanceIdentityPrivateKeySecretReference,
            InstanceIdentityPublicKey = settings.InstanceIdentityPublicKey,
            InstanceIdentityPublicKeyFingerprint = settings.InstanceIdentityPublicKeyFingerprint,
            PairingCode = settings.PairingCode,
            PairingCodeGeneratedAtUtc = settings.PairingCodeGeneratedAtUtc,
            PairingCodeExpiresAtUtc = settings.PairingCodeExpiresAtUtc,
            PortalOrganizationId = settings.PortalOrganizationId,
            PortalOrganizationName = settings.PortalOrganizationName,
            PortalInstanceId = settings.PortalInstanceId,
            PortalApiKeyId = settings.PortalApiKeyId,
            PortalApiSecretReference = settings.PortalApiSecretReference,
            LastConnectionStatus = settings.LastConnectionStatus,
            LastConnectedAtUtc = settings.LastConnectedAtUtc,
            LastHeartbeatAtUtc = settings.LastHeartbeatAtUtc,
            CreatedAtUtc = settings.CreatedAtUtc,
            UpdatedAtUtc = settings.UpdatedAtUtc
        };
}
