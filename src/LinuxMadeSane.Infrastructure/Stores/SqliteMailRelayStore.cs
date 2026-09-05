// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteMailRelayStore(LinuxMadeSaneDbContext dbContext) : IMailRelayStore
{
    public async Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.MailRelayConfigurations
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var entity = entities.MaxBy(item => item.UpdatedUtc);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveConfigurationAsync(
        MailRelayConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MailRelayConfigurations
            .SingleOrDefaultAsync(item => item.Id == configuration.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.MailRelayConfigurations.Add(Map(configuration));
        }
        else
        {
            entity.Enabled = configuration.Enabled;
            entity.RelayHostname = configuration.RelayHostname;
            entity.PublicIpAddress = configuration.PublicIpAddress;
            entity.SubmissionPort = configuration.SubmissionPort;
            entity.AllowTailscale = configuration.AllowTailscale;
            entity.AllowTrustedLan = configuration.AllowTrustedLan;
            entity.AllowPublicSubmission = configuration.AllowPublicSubmission;
            entity.DeliveryMode = (int)configuration.DeliveryMode;
            entity.AllowLegacyPort25 = configuration.AllowLegacyPort25;
            entity.LegacyListenAddressesJson = JsonSerializer.Serialize(configuration.EffectiveLegacyListenAddresses);
            entity.LegacyAllowedNetworksJson = JsonSerializer.Serialize(configuration.EffectiveLegacyAllowedNetworks);
            entity.MonitorPublicIpChanges = configuration.MonitorPublicIpChanges;
            entity.PublicIpCheckIntervalMinutes = configuration.PublicIpCheckIntervalMinutes;
            entity.LastPublicIpCheckUtc = configuration.LastPublicIpCheckUtc;
            entity.LastPublicIpChangeUtc = configuration.LastPublicIpChangeUtc;
            entity.PublicIpMonitorStatus = (int)configuration.PublicIpMonitorStatus;
            entity.PublicIpMonitorDetail = configuration.PublicIpMonitorDetail;
            entity.DefaultMessagesPerMinute = configuration.DefaultMessagesPerMinute;
            entity.DefaultMessagesPerDay = configuration.DefaultMessagesPerDay;
            entity.QueueLimit = configuration.QueueLimit;
            entity.LogRetentionDays = configuration.LogRetentionDays;
            entity.TlsCertificateSecretReference = configuration.TlsCertificateSecretReference;
            entity.TlsPrivateKeySecretReference = configuration.TlsPrivateKeySecretReference;
            entity.CreatedUtc = configuration.CreatedUtc;
            entity.UpdatedUtc = configuration.UpdatedUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.MailRelayDomains
            .AsNoTracking()
            .OrderBy(item => item.DomainName)
            .ToListAsync(cancellationToken))
        .Select(Map)
        .ToArray();

    public async Task SaveDomainAsync(MailRelayDomain domain, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MailRelayDomains
            .SingleOrDefaultAsync(item => item.Id == domain.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.MailRelayDomains.Add(Map(domain));
        }
        else
        {
            Copy(domain, entity);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MailRelayDomains.SingleOrDefaultAsync(item => item.Id == domainId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.MailRelayDomains.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.MailRelayClients
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken))
        .Select(Map)
        .ToArray();

    public async Task SaveClientAsync(MailRelayClient client, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MailRelayClients
            .SingleOrDefaultAsync(item => item.Id == client.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.MailRelayClients.Add(Map(client));
        }
        else
        {
            Copy(client, entity);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MailRelayClients.SingleOrDefaultAsync(item => item.Id == clientId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.MailRelayClients.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(
        Guid domainId,
        CancellationToken cancellationToken = default) =>
        (await dbContext.MailRelayDnsRecords
            .AsNoTracking()
            .Where(item => item.MailRelayDomainId == domainId)
            .OrderBy(item => item.Name)
            .ThenBy(item => item.Type)
            .ToListAsync(cancellationToken))
        .Select(Map)
        .ToArray();

    public async Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MailRelayDnsRecords
            .SingleOrDefaultAsync(item => item.Id == record.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.MailRelayDnsRecords.Add(Map(record));
        }
        else
        {
            entity.MailRelayDomainId = record.MailRelayDomainId;
            entity.CloudflareRecordId = record.CloudflareRecordId;
            entity.Type = record.Type;
            entity.Name = record.Name;
            entity.Purpose = record.Purpose;
            entity.CreatedByLms = record.CreatedByLms;
            entity.ModifiedByLms = record.ModifiedByLms;
            entity.OriginalValue = record.OriginalValue;
            entity.CurrentValue = record.CurrentValue;
            entity.ChangeType = (int)record.ChangeType;
            entity.CreatedUtc = record.CreatedUtc;
            entity.UpdatedUtc = record.UpdatedUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MailRelayDnsRecords.SingleOrDefaultAsync(item => item.Id == recordId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.MailRelayDnsRecords.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static MailRelayConfiguration Map(MailRelayConfigurationEntity entity) =>
        new(
            entity.Id,
            entity.Enabled,
            entity.RelayHostname,
            entity.PublicIpAddress,
            entity.SubmissionPort,
            entity.AllowTailscale,
            entity.AllowTrustedLan,
            entity.AllowPublicSubmission,
            entity.DefaultMessagesPerMinute,
            entity.DefaultMessagesPerDay,
            entity.QueueLimit,
            entity.LogRetentionDays,
            entity.TlsCertificateSecretReference,
            entity.TlsPrivateKeySecretReference,
            entity.CreatedUtc,
            entity.UpdatedUtc,
            (MailRelayDeliveryMode)entity.DeliveryMode,
            entity.AllowLegacyPort25,
            Deserialize(entity.LegacyListenAddressesJson),
            Deserialize(entity.LegacyAllowedNetworksJson))
        {
            MonitorPublicIpChanges = entity.MonitorPublicIpChanges,
            PublicIpCheckIntervalMinutes = entity.PublicIpCheckIntervalMinutes <= 0 ? 60 : entity.PublicIpCheckIntervalMinutes,
            LastPublicIpCheckUtc = entity.LastPublicIpCheckUtc,
            LastPublicIpChangeUtc = entity.LastPublicIpChangeUtc,
            PublicIpMonitorStatus = (MailRelayPublicIpMonitorStatus)entity.PublicIpMonitorStatus,
            PublicIpMonitorDetail = entity.PublicIpMonitorDetail
        };

    private static MailRelayConfigurationEntity Map(MailRelayConfiguration model) =>
        new()
        {
            Id = model.Id,
            Enabled = model.Enabled,
            RelayHostname = model.RelayHostname,
            PublicIpAddress = model.PublicIpAddress,
            SubmissionPort = model.SubmissionPort,
            AllowTailscale = model.AllowTailscale,
            AllowTrustedLan = model.AllowTrustedLan,
            AllowPublicSubmission = model.AllowPublicSubmission,
            DeliveryMode = (int)model.DeliveryMode,
            AllowLegacyPort25 = model.AllowLegacyPort25,
            LegacyListenAddressesJson = JsonSerializer.Serialize(model.EffectiveLegacyListenAddresses),
            LegacyAllowedNetworksJson = JsonSerializer.Serialize(model.EffectiveLegacyAllowedNetworks),
            MonitorPublicIpChanges = model.MonitorPublicIpChanges,
            PublicIpCheckIntervalMinutes = model.PublicIpCheckIntervalMinutes,
            LastPublicIpCheckUtc = model.LastPublicIpCheckUtc,
            LastPublicIpChangeUtc = model.LastPublicIpChangeUtc,
            PublicIpMonitorStatus = (int)model.PublicIpMonitorStatus,
            PublicIpMonitorDetail = model.PublicIpMonitorDetail,
            DefaultMessagesPerMinute = model.DefaultMessagesPerMinute,
            DefaultMessagesPerDay = model.DefaultMessagesPerDay,
            QueueLimit = model.QueueLimit,
            LogRetentionDays = model.LogRetentionDays,
            TlsCertificateSecretReference = model.TlsCertificateSecretReference,
            TlsPrivateKeySecretReference = model.TlsPrivateKeySecretReference,
            CreatedUtc = model.CreatedUtc,
            UpdatedUtc = model.UpdatedUtc
        };

    private static MailRelayDomain Map(MailRelayDomainEntity entity) =>
        new(
            entity.Id,
            entity.MailRelayConfigurationId,
            entity.CloudflareZoneId,
            entity.DomainName,
            entity.Enabled,
            entity.CurrentDkimSelector,
            entity.CurrentDkimPrivateKeySecretReference,
            entity.CurrentDkimCreatedUtc,
            entity.CurrentDkimActivatedUtc,
            entity.PreviousDkimSelector,
            entity.PreviousDkimPrivateKeySecretReference,
            entity.PreviousDkimCreatedUtc,
            entity.PreviousDkimActivatedUtc,
            entity.PreviousDkimRetiredUtc,
            entity.DkimCloudflareRecordId,
            entity.SpfCloudflareRecordId,
            entity.DmarcCloudflareRecordId,
            (MailRelayDnsStatus)entity.SpfStatus,
            (MailRelayDnsStatus)entity.DkimStatus,
            (MailRelayDnsStatus)entity.DmarcStatus,
            (MailRelayDmarcPolicy)entity.DmarcPolicy,
            entity.DmarcReportingAddress,
            entity.CreatedUtc,
            entity.UpdatedUtc);

    private static MailRelayDomainEntity Map(MailRelayDomain model)
    {
        var entity = new MailRelayDomainEntity { Id = model.Id };
        Copy(model, entity);
        return entity;
    }

    private static void Copy(MailRelayDomain model, MailRelayDomainEntity entity)
    {
        entity.MailRelayConfigurationId = model.MailRelayConfigurationId;
        entity.CloudflareZoneId = model.CloudflareZoneId;
        entity.DomainName = model.DomainName;
        entity.Enabled = model.Enabled;
        entity.CurrentDkimSelector = model.CurrentDkimSelector;
        entity.CurrentDkimPrivateKeySecretReference = model.CurrentDkimPrivateKeySecretReference;
        entity.CurrentDkimCreatedUtc = model.CurrentDkimCreatedUtc;
        entity.CurrentDkimActivatedUtc = model.CurrentDkimActivatedUtc;
        entity.PreviousDkimSelector = model.PreviousDkimSelector;
        entity.PreviousDkimPrivateKeySecretReference = model.PreviousDkimPrivateKeySecretReference;
        entity.PreviousDkimCreatedUtc = model.PreviousDkimCreatedUtc;
        entity.PreviousDkimActivatedUtc = model.PreviousDkimActivatedUtc;
        entity.PreviousDkimRetiredUtc = model.PreviousDkimRetiredUtc;
        entity.DkimCloudflareRecordId = model.DkimCloudflareRecordId;
        entity.SpfCloudflareRecordId = model.SpfCloudflareRecordId;
        entity.DmarcCloudflareRecordId = model.DmarcCloudflareRecordId;
        entity.SpfStatus = (int)model.SpfStatus;
        entity.DkimStatus = (int)model.DkimStatus;
        entity.DmarcStatus = (int)model.DmarcStatus;
        entity.DmarcPolicy = (int)model.DmarcPolicy;
        entity.DmarcReportingAddress = model.DmarcReportingAddress;
        entity.CreatedUtc = model.CreatedUtc;
        entity.UpdatedUtc = model.UpdatedUtc;
    }

    private static MailRelayClient Map(MailRelayClientEntity entity) =>
        new(
            entity.Id,
            entity.MailRelayConfigurationId,
            entity.Name,
            entity.Username,
            entity.PasswordHash,
            entity.Enabled,
            Deserialize(entity.AllowedSenderDomainsJson),
            Deserialize(entity.AllowedNetworksJson),
            entity.MessagesPerMinute,
            entity.MessagesPerDay,
            entity.Notes,
            entity.CreatedUtc,
            entity.UpdatedUtc,
            entity.LastUsedUtc);

    private static MailRelayClientEntity Map(MailRelayClient model)
    {
        var entity = new MailRelayClientEntity { Id = model.Id };
        Copy(model, entity);
        return entity;
    }

    private static void Copy(MailRelayClient model, MailRelayClientEntity entity)
    {
        entity.MailRelayConfigurationId = model.MailRelayConfigurationId;
        entity.Name = model.Name;
        entity.Username = model.Username;
        entity.PasswordHash = model.PasswordHash;
        entity.Enabled = model.Enabled;
        entity.AllowedSenderDomainsJson = JsonSerializer.Serialize(model.AllowedSenderDomains);
        entity.AllowedNetworksJson = JsonSerializer.Serialize(model.AllowedNetworks);
        entity.MessagesPerMinute = model.MessagesPerMinute;
        entity.MessagesPerDay = model.MessagesPerDay;
        entity.Notes = model.Notes;
        entity.CreatedUtc = model.CreatedUtc;
        entity.UpdatedUtc = model.UpdatedUtc;
        entity.LastUsedUtc = model.LastUsedUtc;
    }

    private static MailRelayDnsRecord Map(MailRelayDnsRecordEntity entity) =>
        new(
            entity.Id,
            entity.MailRelayDomainId,
            entity.CloudflareRecordId,
            entity.Type,
            entity.Name,
            entity.Purpose,
            entity.CreatedByLms,
            entity.ModifiedByLms,
            entity.OriginalValue,
            entity.CurrentValue,
            (MailRelayDnsChangeType)entity.ChangeType,
            entity.CreatedUtc,
            entity.UpdatedUtc);

    private static MailRelayDnsRecordEntity Map(MailRelayDnsRecord model) =>
        new()
        {
            Id = model.Id,
            MailRelayDomainId = model.MailRelayDomainId,
            CloudflareRecordId = model.CloudflareRecordId,
            Type = model.Type,
            Name = model.Name,
            Purpose = model.Purpose,
            CreatedByLms = model.CreatedByLms,
            ModifiedByLms = model.ModifiedByLms,
            OriginalValue = model.OriginalValue,
            CurrentValue = model.CurrentValue,
            ChangeType = (int)model.ChangeType,
            CreatedUtc = model.CreatedUtc,
            UpdatedUtc = model.UpdatedUtc
        };

    private static IReadOnlyList<string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
