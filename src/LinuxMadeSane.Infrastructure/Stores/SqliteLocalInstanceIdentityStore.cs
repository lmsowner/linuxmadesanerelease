// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteLocalInstanceIdentityStore(LinuxMadeSaneDbContext dbContext) : ILocalInstanceIdentityStore
{
    private const int SingletonId = 1;

    public async Task<LocalInstanceIdentity?> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.LocalInstanceIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(identity => identity.Id == SingletonId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(LocalInstanceIdentity identity, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.LocalInstanceIdentities
            .SingleOrDefaultAsync(existing => existing.Id == SingletonId, cancellationToken);

        if (entity is null)
        {
            dbContext.LocalInstanceIdentities.Add(new LocalInstanceIdentityEntity
            {
                Id = SingletonId,
                InstanceId = identity.InstanceId,
                DisplayName = identity.DisplayName,
                PrivateKeySecretReference = identity.PrivateKeySecretReference,
                PublicKey = identity.PublicKey,
                PublicKeyFingerprint = identity.PublicKeyFingerprint,
                CreatedAtUtc = identity.CreatedAtUtc,
                UpdatedAtUtc = identity.UpdatedAtUtc,
                RegisteredWithPublicSiteAtUtc = identity.RegisteredWithPublicSiteAtUtc
            });
        }
        else
        {
            entity.InstanceId = identity.InstanceId;
            entity.DisplayName = identity.DisplayName;
            entity.PrivateKeySecretReference = identity.PrivateKeySecretReference;
            entity.PublicKey = identity.PublicKey;
            entity.PublicKeyFingerprint = identity.PublicKeyFingerprint;
            entity.CreatedAtUtc = identity.CreatedAtUtc;
            entity.UpdatedAtUtc = identity.UpdatedAtUtc;
            entity.RegisteredWithPublicSiteAtUtc = identity.RegisteredWithPublicSiteAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static LocalInstanceIdentity Map(LocalInstanceIdentityEntity entity) =>
        new(
            entity.InstanceId,
            entity.DisplayName,
            entity.PrivateKeySecretReference,
            entity.PublicKey,
            entity.PublicKeyFingerprint,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.RegisteredWithPublicSiteAtUtc);
}
