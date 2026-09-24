// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteUserManagedHostCredentialProfileStore(LinuxMadeSaneDbContext dbContext)
    : IUserManagedHostCredentialProfileStore
{
    public async Task<IReadOnlyList<UserManagedHostCredentialProfile>> ListAsync(
        Guid userId,
        Guid managedHostId,
        CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.UserManagedHostCredentialProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == userId && profile.ManagedHostId == managedHostId)
            .OrderBy(profile => profile.Name)
            .ToArrayAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<UserManagedHostCredentialProfile?> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.UserManagedHostCredentialProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.Id == profileId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<UserManagedHostCredentialProfile?> FindByNameAsync(
        Guid userId,
        Guid managedHostId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var entity = await dbContext.UserManagedHostCredentialProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                profile => profile.UserId == userId &&
                           profile.ManagedHostId == managedHostId &&
                           profile.NormalizedName == normalizedName,
                cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(
        UserManagedHostCredentialProfile profile,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.UserManagedHostCredentialProfiles
            .SingleOrDefaultAsync(existing => existing.Id == profile.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.UserManagedHostCredentialProfiles.Add(Map(profile));
        }
        else
        {
            Apply(entity, profile);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.UserManagedHostCredentialProfiles
            .SingleOrDefaultAsync(profile => profile.Id == profileId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.UserManagedHostCredentialProfiles.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static UserManagedHostCredentialProfile Map(UserManagedHostCredentialProfileEntity entity) =>
        new(
            entity.Id,
            entity.UserId,
            entity.ManagedHostId,
            entity.Name,
            entity.Username,
            entity.PasswordSecretReference,
            entity.PrivateKeySecretReference,
            entity.PrivateKeyPassphraseSecretReference,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static UserManagedHostCredentialProfileEntity Map(UserManagedHostCredentialProfile profile) =>
        new()
        {
            Id = profile.Id,
            UserId = profile.UserId,
            ManagedHostId = profile.ManagedHostId,
            Name = profile.Name,
            NormalizedName = NormalizeName(profile.Name),
            Username = profile.Username,
            PasswordSecretReference = profile.PasswordSecretReference,
            PrivateKeySecretReference = profile.PrivateKeySecretReference,
            PrivateKeyPassphraseSecretReference = profile.PrivateKeyPassphraseSecretReference,
            CreatedAtUtc = profile.CreatedAtUtc,
            UpdatedAtUtc = profile.UpdatedAtUtc
        };

    private static void Apply(
        UserManagedHostCredentialProfileEntity entity,
        UserManagedHostCredentialProfile profile)
    {
        entity.UserId = profile.UserId;
        entity.ManagedHostId = profile.ManagedHostId;
        entity.Name = profile.Name;
        entity.NormalizedName = NormalizeName(profile.Name);
        entity.Username = profile.Username;
        entity.PasswordSecretReference = profile.PasswordSecretReference;
        entity.PrivateKeySecretReference = profile.PrivateKeySecretReference;
        entity.PrivateKeyPassphraseSecretReference = profile.PrivateKeyPassphraseSecretReference;
        entity.CreatedAtUtc = profile.CreatedAtUtc;
        entity.UpdatedAtUtc = profile.UpdatedAtUtc;
    }

    private static string NormalizeName(string name) =>
        name.Trim().ToUpperInvariant();
}
