// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Security.Cryptography;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteProtectedSecretStore(
    LinuxMadeSaneDbContext dbContext,
    IDataProtectionProvider dataProtectionProvider) : ISecretStore
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("LinuxMadeSane.Secrets.v1");

    public async Task<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ProtectedSecrets
            .AsNoTracking()
            .SingleOrDefaultAsync(secret => secret.Reference == secretReference, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(entity.ProtectedValue);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The requested secret could not be unprotected.", exception);
        }
    }

    public async Task<SecretReferenceMetadata?> GetMetadataAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ProtectedSecrets
            .AsNoTracking()
            .SingleOrDefaultAsync(secret => secret.Reference == secretReference, cancellationToken);

        return entity is null
            ? null
            : new SecretReferenceMetadata(entity.Reference, entity.Purpose, true);
    }

    public async Task<string> StoreSecretAsync(
        string secretValue,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new ProtectedSecretEntity
        {
            Reference = $"secret://{Guid.NewGuid():N}",
            Purpose = purpose.Trim(),
            ProtectedValue = protector.Protect(secretValue),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.ProtectedSecrets.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.Reference;
    }

    public async Task DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ProtectedSecrets
            .SingleOrDefaultAsync(secret => secret.Reference == secretReference, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.ProtectedSecrets.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
