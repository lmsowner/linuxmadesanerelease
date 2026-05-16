// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteAiProviderSettingsStore(LinuxMadeSaneDbContext dbContext) : IAiProviderSettingsStore
{
    public async Task<IReadOnlyList<AiProviderSettings>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.AiProviderSettings
            .AsNoTracking()
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.DisplayName)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task<AiProviderSettings?> GetAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiProviderSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                provider => provider.ProviderKey == providerKey,
                cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(AiProviderSettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiProviderSettings
            .SingleOrDefaultAsync(
                provider => provider.ProviderKey == settings.ProviderKey,
                cancellationToken);

        if (entity is null)
        {
            dbContext.AiProviderSettings.Add(Map(settings));
        }
        else
        {
            entity.ProviderType = (int)settings.ProviderType;
            entity.DisplayName = settings.DisplayName;
            entity.IsEnabled = settings.IsEnabled;
            entity.IsDefault = settings.IsDefault;
            entity.BaseUrl = settings.BaseUrl;
            entity.DefaultModelId = settings.DefaultModelId;
            entity.StreamingEnabled = settings.StreamingEnabled;
            entity.ToolUseEnabled = settings.ToolUseEnabled;
            entity.Notes = settings.Notes;
            entity.MetadataJson = settings.MetadataJson;
            entity.ApiKeySecretReference = settings.ApiKeySecretReference;
            entity.CreatedAtUtc = settings.CreatedAtUtc;
            entity.UpdatedAtUtc = settings.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AiProviderSettings Map(AiProviderSettingsEntity entity) =>
        new(
            entity.ProviderKey,
            (AiProviderType)entity.ProviderType,
            entity.DisplayName,
            entity.IsEnabled,
            entity.IsDefault,
            entity.BaseUrl,
            entity.DefaultModelId,
            entity.StreamingEnabled,
            entity.ToolUseEnabled,
            entity.Notes,
            entity.MetadataJson,
            entity.ApiKeySecretReference,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static AiProviderSettingsEntity Map(AiProviderSettings settings) =>
        new()
        {
            ProviderKey = settings.ProviderKey,
            ProviderType = (int)settings.ProviderType,
            DisplayName = settings.DisplayName,
            IsEnabled = settings.IsEnabled,
            IsDefault = settings.IsDefault,
            BaseUrl = settings.BaseUrl,
            DefaultModelId = settings.DefaultModelId,
            StreamingEnabled = settings.StreamingEnabled,
            ToolUseEnabled = settings.ToolUseEnabled,
            Notes = settings.Notes,
            MetadataJson = settings.MetadataJson,
            ApiKeySecretReference = settings.ApiKeySecretReference,
            CreatedAtUtc = settings.CreatedAtUtc,
            UpdatedAtUtc = settings.UpdatedAtUtc
        };
}
