// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Messaging;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteMessagingEmailSettingsStore(LinuxMadeSaneDbContext dbContext) : IMessagingEmailSettingsStore
{
    private const int SettingsRowId = 1;

    public async Task<MessagingEmailSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MessagingEmailSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == SettingsRowId, cancellationToken);
        if (entity is not null)
        {
            return Map(entity);
        }

        return MessagingEmailSettings.CreateDefault(DateTimeOffset.UtcNow);
    }

    public async Task SaveAsync(MessagingEmailSettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.MessagingEmailSettings
            .SingleOrDefaultAsync(item => item.Id == SettingsRowId, cancellationToken);
        if (entity is null)
        {
            dbContext.MessagingEmailSettings.Add(Map(settings));
        }
        else
        {
            Apply(entity, settings);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static MessagingEmailSettings Map(MessagingEmailSettingsEntity entity) =>
        new(
            entity.Id,
            entity.IsEnabled,
            Enum.IsDefined(typeof(MessagingEmailProvider), entity.Provider)
                ? (MessagingEmailProvider)entity.Provider
                : MessagingEmailProvider.Disabled,
            entity.SenderAddress,
            entity.SenderDisplayName,
            entity.SmtpHost,
            entity.SmtpPort,
            entity.SmtpUseStartTls,
            entity.SmtpUsername,
            entity.SmtpPasswordSecretReference,
            entity.GraphTenantId,
            entity.GraphClientId,
            entity.GraphClientSecretReference,
            entity.GraphAuthority,
            entity.GraphBaseUrl,
            entity.GraphSaveToSentItems,
            entity.LastVerifiedAtUtc,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static MessagingEmailSettingsEntity Map(MessagingEmailSettings settings) =>
        new()
        {
            Id = SettingsRowId,
            IsEnabled = settings.IsEnabled,
            Provider = (int)settings.Provider,
            SenderAddress = settings.SenderAddress,
            SenderDisplayName = settings.SenderDisplayName,
            SmtpHost = settings.SmtpHost,
            SmtpPort = settings.SmtpPort,
            SmtpUseStartTls = settings.SmtpUseStartTls,
            SmtpUsername = settings.SmtpUsername,
            SmtpPasswordSecretReference = settings.SmtpPasswordSecretReference,
            GraphTenantId = settings.GraphTenantId,
            GraphClientId = settings.GraphClientId,
            GraphClientSecretReference = settings.GraphClientSecretReference,
            GraphAuthority = settings.GraphAuthority,
            GraphBaseUrl = settings.GraphBaseUrl,
            GraphSaveToSentItems = settings.GraphSaveToSentItems,
            LastVerifiedAtUtc = settings.LastVerifiedAtUtc,
            CreatedAtUtc = settings.CreatedAtUtc,
            UpdatedAtUtc = settings.UpdatedAtUtc
        };

    private static void Apply(MessagingEmailSettingsEntity entity, MessagingEmailSettings settings)
    {
        entity.IsEnabled = settings.IsEnabled;
        entity.Provider = (int)settings.Provider;
        entity.SenderAddress = settings.SenderAddress;
        entity.SenderDisplayName = settings.SenderDisplayName;
        entity.SmtpHost = settings.SmtpHost;
        entity.SmtpPort = settings.SmtpPort;
        entity.SmtpUseStartTls = settings.SmtpUseStartTls;
        entity.SmtpUsername = settings.SmtpUsername;
        entity.SmtpPasswordSecretReference = settings.SmtpPasswordSecretReference;
        entity.GraphTenantId = settings.GraphTenantId;
        entity.GraphClientId = settings.GraphClientId;
        entity.GraphClientSecretReference = settings.GraphClientSecretReference;
        entity.GraphAuthority = settings.GraphAuthority;
        entity.GraphBaseUrl = settings.GraphBaseUrl;
        entity.GraphSaveToSentItems = settings.GraphSaveToSentItems;
        entity.LastVerifiedAtUtc = settings.LastVerifiedAtUtc;
        entity.UpdatedAtUtc = settings.UpdatedAtUtc;
    }
}
