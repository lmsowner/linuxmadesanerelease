using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteSecurityPasskeyStore(LinuxMadeSaneDbContext dbContext) : ISecurityPasskeyStore
{
    public async Task<IReadOnlyList<SecurityPasskeyCredential>> ListByUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var credentials = await dbContext.SecurityPasskeyCredentials
            .AsNoTracking()
            .Where(credential => credential.UserId == userId)
            .Select(credential => Map(credential))
            .ToArrayAsync(cancellationToken);

        return credentials
            .OrderBy(credential => credential.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(credential => credential.CreatedAtUtc)
            .ToArray();
    }

    public async Task<SecurityPasskeyCredential?> GetByCredentialIdAsync(
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        var normalizedCredentialId = credentialId.Trim();
        var entity = await dbContext.SecurityPasskeyCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(
                credential => credential.CredentialId == normalizedCredentialId,
                cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public Task<bool> CredentialIdExistsAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        var normalizedCredentialId = credentialId.Trim();
        return dbContext.SecurityPasskeyCredentials
            .AsNoTracking()
            .AnyAsync(credential => credential.CredentialId == normalizedCredentialId, cancellationToken);
    }

    public async Task SaveAsync(SecurityPasskeyCredential credential, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SecurityPasskeyCredentials
            .SingleOrDefaultAsync(existing => existing.Id == credential.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.SecurityPasskeyCredentials.Add(new SecurityPasskeyCredentialEntity
            {
                Id = credential.Id,
                UserId = credential.UserId,
                CredentialId = credential.CredentialId,
                PublicKey = credential.PublicKey,
                UserHandle = credential.UserHandle,
                SignatureCounter = credential.SignatureCounter,
                FriendlyName = credential.FriendlyName,
                IsBackedUp = credential.IsBackedUp,
                CreatedAtUtc = credential.CreatedAtUtc,
                UpdatedAtUtc = credential.UpdatedAtUtc,
                LastUsedAtUtc = credential.LastUsedAtUtc
            });
        }
        else
        {
            entity.UserId = credential.UserId;
            entity.CredentialId = credential.CredentialId;
            entity.PublicKey = credential.PublicKey;
            entity.UserHandle = credential.UserHandle;
            entity.SignatureCounter = credential.SignatureCounter;
            entity.FriendlyName = credential.FriendlyName;
            entity.IsBackedUp = credential.IsBackedUp;
            entity.CreatedAtUtc = credential.CreatedAtUtc;
            entity.UpdatedAtUtc = credential.UpdatedAtUtc;
            entity.LastUsedAtUtc = credential.LastUsedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SecurityPasskeyCredentials
            .SingleOrDefaultAsync(credential => credential.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.SecurityPasskeyCredentials.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static SecurityPasskeyCredential Map(SecurityPasskeyCredentialEntity entity) =>
        new(
            entity.Id,
            entity.UserId,
            entity.CredentialId,
            entity.PublicKey,
            entity.UserHandle,
            entity.SignatureCounter,
            entity.FriendlyName,
            entity.IsBackedUp,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.LastUsedAtUtc);
}
