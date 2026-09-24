// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteSavedCommandStore(LinuxMadeSaneDbContext dbContext) : ISavedCommandStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SavedCommand>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.SavedCommands
            .AsNoTracking()
            .OrderBy(command => command.Name)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<SavedCommand>> ListByHostAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        var items = await dbContext.SavedCommands
            .AsNoTracking()
            .Where(command => command.HostId == hostId)
            .OrderBy(command => command.Name)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task<SavedCommand?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SavedCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(command => command.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(SavedCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SavedCommands
            .SingleOrDefaultAsync(existing => existing.Id == command.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.SavedCommands.Add(new SavedCommandEntity
            {
                Id = command.Id,
                HostId = command.HostId,
                Name = command.Name,
                CommandText = command.CommandText,
                Description = command.Description,
                RequiresSudo = command.RequiresSudo,
                IsQuickAccess = command.IsQuickAccess,
                IsGlobalFavorite = command.IsGlobalFavorite,
                IsTemplate = command.IsTemplate,
                TemplateSourceId = command.TemplateSourceId,
                LinkGroupId = command.LinkGroupId,
                ParameterDefinitionsJson = Serialize(command.ParameterDefinitions ?? []),
                ParameterValueSnapshotJson = Serialize(command.ParameterValueSnapshot ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            });
        }
        else
        {
            entity.HostId = command.HostId;
            entity.Name = command.Name;
            entity.CommandText = command.CommandText;
            entity.Description = command.Description;
            entity.RequiresSudo = command.RequiresSudo;
            entity.IsQuickAccess = command.IsQuickAccess;
            entity.IsGlobalFavorite = command.IsGlobalFavorite;
            entity.IsTemplate = command.IsTemplate;
            entity.TemplateSourceId = command.TemplateSourceId;
            entity.LinkGroupId = command.LinkGroupId;
            entity.ParameterDefinitionsJson = Serialize(command.ParameterDefinitions ?? []);
            entity.ParameterValueSnapshotJson = Serialize(command.ParameterValueSnapshot ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.SavedCommands
            .SingleOrDefaultAsync(existing => existing.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        dbContext.SavedCommands.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static SavedCommand Map(SavedCommandEntity entity) =>
        new(
            entity.Id,
            entity.HostId,
            entity.Name,
            entity.CommandText,
            entity.Description,
            entity.RequiresSudo,
            entity.IsQuickAccess,
            entity.IsTemplate,
            entity.TemplateSourceId,
            entity.LinkGroupId,
            DeserializeDefinitions(entity.ParameterDefinitionsJson),
            DeserializeValues(entity.ParameterValueSnapshotJson),
            entity.IsGlobalFavorite);

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

    private static IReadOnlyList<RunbookParameterDefinition> DeserializeDefinitions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<RunbookParameterDefinition>>(json, SerializerOptions) ?? [];
    }

    private static IReadOnlyDictionary<string, string> DeserializeValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var items = JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(items, StringComparer.OrdinalIgnoreCase);
    }
}
