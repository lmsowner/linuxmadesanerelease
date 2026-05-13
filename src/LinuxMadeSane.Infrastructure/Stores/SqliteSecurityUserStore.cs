using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteSecurityUserStore(LinuxMadeSaneDbContext dbContext) : ISecurityUserStore
{
    public async Task<IReadOnlyList<SecurityUser>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.SecurityUsers
            .AsNoTracking()
            .OrderBy(user => user.Email)
            .Select(user => Map(user))
            .ToArrayAsync(cancellationToken);

    public async Task<SecurityUser?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SecurityUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<SecurityUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var entity = await dbContext.SecurityUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<SecurityUser?> FindByLinuxUsernameAsync(string linuxUsername, CancellationToken cancellationToken = default)
    {
        var normalizedUsername = linuxUsername.Trim();
        var entity = await dbContext.SecurityUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.LinuxUsername == normalizedUsername, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(SecurityUser user, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SecurityUsers
            .SingleOrDefaultAsync(existing => existing.Id == user.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.SecurityUsers.Add(new SecurityUserEntity
            {
                Id = user.Id,
                Email = user.Email,
                NormalizedEmail = user.Email.Trim().ToUpperInvariant(),
                LinuxUsername = user.LinuxUsername,
                IsEnabled = user.IsEnabled,
                SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes),
                SshAuthenticationMode = (int)user.SshAuthenticationMode,
                AuthorizedKeyEntries = user.AuthorizedKeyEntries,
                IsLocalAccountManaged = user.IsLocalAccountManaged,
                OtpSecretReference = user.OtpSecretReference,
                CreatedAtUtc = user.CreatedAtUtc,
                UpdatedAtUtc = user.UpdatedAtUtc,
                LastLoginAtUtc = user.LastLoginAtUtc,
                PasswordChangedAtUtc = user.PasswordChangedAtUtc
            });
        }
        else
        {
            entity.Email = user.Email;
            entity.NormalizedEmail = user.Email.Trim().ToUpperInvariant();
            entity.LinuxUsername = user.LinuxUsername;
            entity.IsEnabled = user.IsEnabled;
            entity.SessionLifetimeMinutes = SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(user.SessionLifetimeMinutes);
            entity.SshAuthenticationMode = (int)user.SshAuthenticationMode;
            entity.AuthorizedKeyEntries = user.AuthorizedKeyEntries;
            entity.IsLocalAccountManaged = user.IsLocalAccountManaged;
            entity.OtpSecretReference = user.OtpSecretReference;
            entity.CreatedAtUtc = user.CreatedAtUtc;
            entity.UpdatedAtUtc = user.UpdatedAtUtc;
            entity.LastLoginAtUtc = user.LastLoginAtUtc;
            entity.PasswordChangedAtUtc = user.PasswordChangedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SecurityUsers
            .SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.SecurityUsers.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static SecurityUser Map(SecurityUserEntity entity) =>
        new(
            entity.Id,
            entity.Email,
            entity.LinuxUsername,
            entity.IsEnabled,
            SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(entity.SessionLifetimeMinutes),
            Enum.IsDefined(typeof(RemoteAccessSshAuthenticationMode), entity.SshAuthenticationMode)
                ? (RemoteAccessSshAuthenticationMode)entity.SshAuthenticationMode
                : RemoteAccessSshAuthenticationMode.Password,
            entity.AuthorizedKeyEntries,
            entity.IsLocalAccountManaged,
            entity.OtpSecretReference,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.LastLoginAtUtc,
            entity.PasswordChangedAtUtc);
}
