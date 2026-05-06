using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteManagedHostStore(LinuxMadeSaneDbContext dbContext) : IManagedHostStore
{
    public async Task<IReadOnlyList<ManagedHost>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.ManagedHosts
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return items
            .Select(Map)
            .OrderBy(ManagedHostCapabilities.GetSortRank)
            .ThenBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ManagedHost?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ManagedHosts
            .AsNoTracking()
            .SingleOrDefaultAsync(host => host.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(ManagedHost host, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ManagedHosts
            .SingleOrDefaultAsync(existing => existing.Id == host.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.ManagedHosts.Add(Map(host));
        }
        else
        {
            entity.Name = host.Name;
            entity.Hostname = host.Hostname;
            entity.Port = host.Port;
            entity.Environment = host.Environment;
            entity.Description = host.Description;
            entity.DefaultWorkingDirectory = host.DefaultWorkingDirectory;
            entity.OperatingStatus = (int)host.OperatingStatus;
            entity.PrimaryAuthenticationType = (int)host.PrimaryAuthenticationType;
            entity.FallbackAuthenticationType = host.FallbackAuthenticationType.HasValue
                ? (int)host.FallbackAuthenticationType.Value
                : null;
            entity.Username = host.Username;
            entity.PasswordSecretReference = host.PasswordSecretReference;
            entity.PrivateKeySecretReference = host.PrivateKeySecretReference;
            entity.PrivateKeyPassphraseSecretReference = host.PrivateKeyPassphraseSecretReference;
            entity.UseKeyboardInteractiveFallback = host.UseKeyboardInteractiveFallback;
            entity.LastSeenUtc = host.LastSeenUtc;
            entity.LastConnectionTestStatus = (int)host.LastConnectionTestStatus;
            entity.Platform = host.Platform;
            entity.Kind = (int)host.Kind;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ManagedHost Map(ManagedHostEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.Hostname,
            entity.Port,
            entity.Environment,
            entity.Description,
            entity.DefaultWorkingDirectory,
            (HostOperatingStatus)entity.OperatingStatus,
            (AuthenticationType)entity.PrimaryAuthenticationType,
            entity.FallbackAuthenticationType.HasValue ? (AuthenticationType)entity.FallbackAuthenticationType.Value : null,
            entity.Username,
            entity.PasswordSecretReference,
            entity.PrivateKeySecretReference,
            entity.PrivateKeyPassphraseSecretReference,
            entity.UseKeyboardInteractiveFallback,
            entity.LastSeenUtc,
            (ConnectionTestStatus)entity.LastConnectionTestStatus,
            entity.Platform,
            (ManagedHostKind)entity.Kind);

    private static ManagedHostEntity Map(ManagedHost host) =>
        new()
        {
            Id = host.Id,
            Name = host.Name,
            Hostname = host.Hostname,
            Port = host.Port,
            Environment = host.Environment,
            Description = host.Description,
            DefaultWorkingDirectory = host.DefaultWorkingDirectory,
            OperatingStatus = (int)host.OperatingStatus,
            PrimaryAuthenticationType = (int)host.PrimaryAuthenticationType,
            FallbackAuthenticationType = host.FallbackAuthenticationType.HasValue
                ? (int)host.FallbackAuthenticationType.Value
                : null,
            Username = host.Username,
            PasswordSecretReference = host.PasswordSecretReference,
            PrivateKeySecretReference = host.PrivateKeySecretReference,
            PrivateKeyPassphraseSecretReference = host.PrivateKeyPassphraseSecretReference,
            UseKeyboardInteractiveFallback = host.UseKeyboardInteractiveFallback,
            LastSeenUtc = host.LastSeenUtc,
            LastConnectionTestStatus = (int)host.LastConnectionTestStatus,
            Platform = host.Platform,
            Kind = (int)host.Kind
        };
}
