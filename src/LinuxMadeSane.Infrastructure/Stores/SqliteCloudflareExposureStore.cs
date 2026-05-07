using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteCloudflareExposureStore(LinuxMadeSaneDbContext dbContext) : ICloudflareExposureStore
{
    public async Task<CloudflareSettings?> GetSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.CloudflareSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ManagedHostId == managedHostId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveSettingsAsync(CloudflareSettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.CloudflareSettings
            .SingleOrDefaultAsync(item => item.ManagedHostId == settings.ManagedHostId, cancellationToken);

        if (entity is null)
        {
            dbContext.CloudflareSettings.Add(Map(settings));
        }
        else
        {
            entity.AccountId = settings.AccountId;
            entity.AccountName = settings.AccountName;
            entity.ZoneId = settings.ZoneId;
            entity.ZoneName = settings.ZoneName;
            entity.ApiTokenSecretReference = settings.ApiTokenSecretReference;
            entity.CreatedAtUtc = settings.CreatedAtUtc;
            entity.UpdatedAtUtc = settings.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.CloudflareSettings
            .SingleOrDefaultAsync(item => item.ManagedHostId == managedHostId, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.CloudflareSettings.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExposedServiceConfig>> ListConfigsAsync(Guid managedHostId, CancellationToken cancellationToken = default)
    {
        var items = await dbContext.ExposedServiceConfigs
            .AsNoTracking()
            .Where(item => item.ManagedHostId == managedHostId)
            .OrderBy(item => item.Hostname)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task<ExposedServiceConfig?> GetConfigAsync(Guid configId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ExposedServiceConfigs
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == configId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<ExposedServiceConfig?> GetConfigByHostnameAsync(
        Guid managedHostId,
        string hostname,
        CancellationToken cancellationToken = default)
    {
        var normalizedHostname = hostname.Trim();
        var entity = await dbContext.ExposedServiceConfigs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ManagedHostId == managedHostId &&
                        item.Hostname == normalizedHostname,
                cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveConfigAsync(ExposedServiceConfig config, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ExposedServiceConfigs
            .SingleOrDefaultAsync(item => item.Id == config.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.ExposedServiceConfigs.Add(Map(config));
        }
        else
        {
            entity.ManagedHostId = config.ManagedHostId;
            entity.ServiceName = config.ServiceName;
            entity.AccountId = config.AccountId;
            entity.AccountName = config.AccountName;
            entity.ZoneId = config.ZoneId;
            entity.ZoneName = config.ZoneName;
            entity.Hostname = config.Hostname;
            entity.LocalServiceUrl = config.LocalServiceUrl;
            entity.TunnelId = config.TunnelId;
            entity.TunnelName = config.TunnelName;
            entity.DnsRecordId = config.DnsRecordId;
            entity.AccessApplicationId = config.AccessApplicationId;
            entity.AccessPolicyId = config.AccessPolicyId;
            entity.AccessMode = (int)config.AccessMode;
            entity.AllowedEmailsJson = Serialize(config.AllowedEmails);
            entity.AllowedEmailDomainsJson = Serialize(config.AllowedEmailDomains);
            entity.OriginRequestSettingsJson = SerializeOriginRequestSettings(config.OriginRequestSettings);
            entity.CreatedAtUtc = config.CreatedAtUtc;
            entity.UpdatedAtUtc = config.UpdatedAtUtc;
            entity.DisabledAtUtc = config.DisabledAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteConfigAsync(Guid configId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ExposedServiceConfigs
            .SingleOrDefaultAsync(item => item.Id == configId, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.ExposedServiceConfigs.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static CloudflareSettings Map(CloudflareSettingsEntity entity) =>
        new(
            entity.ManagedHostId,
            entity.AccountId,
            entity.AccountName,
            entity.ZoneId,
            entity.ZoneName,
            entity.ApiTokenSecretReference,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static CloudflareSettingsEntity Map(CloudflareSettings settings) =>
        new()
        {
            ManagedHostId = settings.ManagedHostId,
            AccountId = settings.AccountId,
            AccountName = settings.AccountName,
            ZoneId = settings.ZoneId,
            ZoneName = settings.ZoneName,
            ApiTokenSecretReference = settings.ApiTokenSecretReference,
            CreatedAtUtc = settings.CreatedAtUtc,
            UpdatedAtUtc = settings.UpdatedAtUtc
        };

    private static ExposedServiceConfig Map(ExposedServiceConfigEntity entity) =>
        new(
            entity.Id,
            entity.ManagedHostId,
            entity.ServiceName,
            entity.AccountId,
            entity.AccountName,
            entity.ZoneId,
            entity.ZoneName,
            entity.Hostname,
            entity.LocalServiceUrl,
            entity.TunnelId,
            entity.TunnelName,
            entity.DnsRecordId,
            entity.AccessApplicationId,
            entity.AccessPolicyId,
            (ExposedServiceAccessMode)entity.AccessMode,
            Deserialize(entity.AllowedEmailsJson),
            Deserialize(entity.AllowedEmailDomainsJson),
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.DisabledAtUtc,
            DeserializeOriginRequestSettings(entity.OriginRequestSettingsJson));

    private static ExposedServiceConfigEntity Map(ExposedServiceConfig config) =>
        new()
        {
            Id = config.Id,
            ManagedHostId = config.ManagedHostId,
            ServiceName = config.ServiceName,
            AccountId = config.AccountId,
            AccountName = config.AccountName,
            ZoneId = config.ZoneId,
            ZoneName = config.ZoneName,
            Hostname = config.Hostname,
            LocalServiceUrl = config.LocalServiceUrl,
            TunnelId = config.TunnelId,
            TunnelName = config.TunnelName,
            DnsRecordId = config.DnsRecordId,
            AccessApplicationId = config.AccessApplicationId,
            AccessPolicyId = config.AccessPolicyId,
            AccessMode = (int)config.AccessMode,
            AllowedEmailsJson = Serialize(config.AllowedEmails),
            AllowedEmailDomainsJson = Serialize(config.AllowedEmailDomains),
            OriginRequestSettingsJson = SerializeOriginRequestSettings(config.OriginRequestSettings),
            CreatedAtUtc = config.CreatedAtUtc,
            UpdatedAtUtc = config.UpdatedAtUtc,
            DisabledAtUtc = config.DisabledAtUtc
        };

    private static string Serialize(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values);

    private static IReadOnlyList<string> Deserialize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();

    private static string SerializeOriginRequestSettings(CloudflareOriginRequestSettings? settings) =>
        JsonSerializer.Serialize(settings ?? CloudflareOriginRequestSettings.Default);

    private static CloudflareOriginRequestSettings DeserializeOriginRequestSettings(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? CloudflareOriginRequestSettings.Default
            : JsonSerializer.Deserialize<CloudflareOriginRequestSettings>(value) ?? CloudflareOriginRequestSettings.Default;
}
