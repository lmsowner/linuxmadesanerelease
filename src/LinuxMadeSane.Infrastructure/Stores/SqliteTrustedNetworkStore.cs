using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteTrustedNetworkStore(LinuxMadeSaneDbContext dbContext) : ITrustedNetworkStore
{
    public async Task<IReadOnlyList<TrustedNetworkEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.TrustedNetworkEntries
            .AsNoTracking()
            .OrderByDescending(entry => entry.IsBuiltIn)
            .ThenBy(entry => entry.Label)
            .Select(entry => Map(entry))
            .ToArrayAsync(cancellationToken);

    public async Task<TrustedNetworkEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.TrustedNetworkEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(TrustedNetworkEntry entry, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.TrustedNetworkEntries
            .SingleOrDefaultAsync(existing => existing.Id == entry.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.TrustedNetworkEntries.Add(new TrustedNetworkEntryEntity
            {
                Id = entry.Id,
                Label = entry.Label,
                AddressOrCidr = entry.AddressOrCidr,
                Description = entry.Description,
                IsEnabled = entry.IsEnabled,
                IsTrustedAccessEnabled = entry.IsTrustedAccessEnabled,
                IsAuthenticationEnabled = entry.IsAuthenticationEnabled,
                IsBuiltIn = entry.IsBuiltIn,
                CreatedAtUtc = entry.CreatedAtUtc,
                UpdatedAtUtc = entry.UpdatedAtUtc
            });
        }
        else
        {
            entity.Label = entry.Label;
            entity.AddressOrCidr = entry.AddressOrCidr;
            entity.Description = entry.Description;
            entity.IsEnabled = entry.IsEnabled;
            entity.IsTrustedAccessEnabled = entry.IsTrustedAccessEnabled;
            entity.IsAuthenticationEnabled = entry.IsAuthenticationEnabled;
            entity.IsBuiltIn = entry.IsBuiltIn;
            entity.CreatedAtUtc = entry.CreatedAtUtc;
            entity.UpdatedAtUtc = entry.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.TrustedNetworkEntries
            .SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.TrustedNetworkEntries.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static TrustedNetworkEntry Map(TrustedNetworkEntryEntity entity) =>
        new(
            entity.Id,
            entity.Label,
            entity.AddressOrCidr,
            entity.Description,
            entity.IsEnabled,
            entity.IsTrustedAccessEnabled,
            entity.IsAuthenticationEnabled,
            entity.IsBuiltIn,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
}
