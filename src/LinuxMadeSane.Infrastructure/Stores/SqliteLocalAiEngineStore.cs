using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.LocalAi;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteLocalAiEngineStore(LinuxMadeSaneDbContext dbContext) : ILocalAiEngineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int SettingsRowId = 1;

    public async Task<LocalAiEngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.LocalAiEngineSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SettingsRowId, cancellationToken);

        if (entity is null)
        {
            var now = DateTimeOffset.UtcNow;
            return new LocalAiEngineSettings(
                LocalAiRuntimeKind.Ollama,
                "http://127.0.0.1:11434",
                "qwen2.5-coder:1.5b",
                "local-ollama",
                false,
                true,
                [],
                [],
                [],
                2,
                4,
                30,
                24000,
                120,
                now,
                now,
                null);
        }

        return Map(entity);
    }

    public async Task SaveSettingsAsync(LocalAiEngineSettings settings, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.LocalAiEngineSettings
            .SingleOrDefaultAsync(item => item.Id == SettingsRowId, cancellationToken);

        if (entity is null)
        {
            dbContext.LocalAiEngineSettings.Add(Map(settings));
        }
        else
        {
            Apply(entity, settings);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalAiInstalledModel>> ListInstalledModelsAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.LocalAiInstalledModels
                .AsNoTracking()
                .OrderBy(item => item.DisplayName)
                .ToListAsync(cancellationToken))
            .Select(Map)
            .ToArray();

    public async Task ReplaceInstalledModelsAsync(IReadOnlyList<LocalAiInstalledModel> models, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.LocalAiInstalledModels.ToListAsync(cancellationToken);
        dbContext.LocalAiInstalledModels.RemoveRange(existing);
        dbContext.LocalAiInstalledModels.AddRange(models.Select(Map));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LocalAiHardwareProfile?> GetLatestHardwareProfileAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.LocalAiHardwareSnapshots
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CapturedAtUtc)
            .Select(Map)
            .FirstOrDefault();

    public async Task SaveHardwareProfileAsync(LocalAiHardwareProfile profile, CancellationToken cancellationToken = default)
    {
        dbContext.LocalAiHardwareSnapshots.Add(Map(profile));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalAiBenchmarkResult>> ListBenchmarkResultsAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.LocalAiBenchmarkResults
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.ExecutedAtUtc)
            .Select(Map)
            .ToArray();

    public async Task SaveBenchmarkResultAsync(LocalAiBenchmarkResult result, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.LocalAiBenchmarkResults
            .SingleOrDefaultAsync(item => item.Id == result.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.LocalAiBenchmarkResults.Add(Map(result));
        }
        else
        {
            entity.ModelId = result.ModelId;
            entity.PromptSummary = result.PromptSummary;
            entity.Succeeded = result.Succeeded;
            entity.DurationMilliseconds = (long)result.Duration.TotalMilliseconds;
            entity.Detail = result.Detail;
            entity.ExecutedAtUtc = result.ExecutedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalAiUsageEntry>> ListUsageAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.LocalAiUsageEntries
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CompletedAtUtc)
            .Select(Map)
            .ToArray();

    public async Task SaveUsageAsync(LocalAiUsageEntry entry, CancellationToken cancellationToken = default)
    {
        dbContext.LocalAiUsageEntries.Add(Map(entry));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalAiAuditEntry>> ListAuditEntriesAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.LocalAiAuditEntries
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Select(Map)
            .ToArray();

    public async Task SaveAuditEntryAsync(LocalAiAuditEntry entry, CancellationToken cancellationToken = default)
    {
        dbContext.LocalAiAuditEntries.Add(Map(entry));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static LocalAiEngineSettings Map(LocalAiEngineSettingsEntity entity) =>
        new(
            (LocalAiRuntimeKind)entity.RuntimeKind,
            entity.RuntimeEndpoint,
            entity.DefaultModelId,
            entity.LocalProviderKey,
            entity.SharingEnabled,
            entity.AllowOrganizationInstances,
            DeserializeGuids(entity.AllowedOrganizationIdsJson),
            DeserializeGuids(entity.AllowedInstanceIdsJson),
            DeserializeStrings(entity.AllowedModelIdsJson),
            entity.MaxConcurrentRequests,
            entity.MaxQueuedRequests,
            entity.MaxRequestsPerMinute,
            entity.MaxPromptCharacters,
            entity.RequestTimeoutSeconds,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.LastSharedAtUtc);

    private static LocalAiEngineSettingsEntity Map(LocalAiEngineSettings settings) =>
        new()
        {
            Id = SettingsRowId,
            RuntimeKind = (int)settings.RuntimeKind,
            RuntimeEndpoint = settings.RuntimeEndpoint,
            DefaultModelId = settings.DefaultModelId,
            LocalProviderKey = settings.LocalProviderKey,
            SharingEnabled = settings.SharingEnabled,
            AllowOrganizationInstances = settings.AllowOrganizationInstances,
            AllowedOrganizationIdsJson = JsonSerializer.Serialize(settings.AllowedOrganizationIds, JsonOptions),
            AllowedInstanceIdsJson = JsonSerializer.Serialize(settings.AllowedInstanceIds, JsonOptions),
            AllowedModelIdsJson = JsonSerializer.Serialize(settings.AllowedModelIds, JsonOptions),
            MaxConcurrentRequests = settings.MaxConcurrentRequests,
            MaxQueuedRequests = settings.MaxQueuedRequests,
            MaxRequestsPerMinute = settings.MaxRequestsPerMinute,
            MaxPromptCharacters = settings.MaxPromptCharacters,
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
            CreatedAtUtc = settings.CreatedAtUtc,
            UpdatedAtUtc = settings.UpdatedAtUtc,
            LastSharedAtUtc = settings.LastSharedAtUtc
        };

    private static void Apply(LocalAiEngineSettingsEntity entity, LocalAiEngineSettings settings)
    {
        entity.RuntimeKind = (int)settings.RuntimeKind;
        entity.RuntimeEndpoint = settings.RuntimeEndpoint;
        entity.DefaultModelId = settings.DefaultModelId;
        entity.LocalProviderKey = settings.LocalProviderKey;
        entity.SharingEnabled = settings.SharingEnabled;
        entity.AllowOrganizationInstances = settings.AllowOrganizationInstances;
        entity.AllowedOrganizationIdsJson = JsonSerializer.Serialize(settings.AllowedOrganizationIds, JsonOptions);
        entity.AllowedInstanceIdsJson = JsonSerializer.Serialize(settings.AllowedInstanceIds, JsonOptions);
        entity.AllowedModelIdsJson = JsonSerializer.Serialize(settings.AllowedModelIds, JsonOptions);
        entity.MaxConcurrentRequests = settings.MaxConcurrentRequests;
        entity.MaxQueuedRequests = settings.MaxQueuedRequests;
        entity.MaxRequestsPerMinute = settings.MaxRequestsPerMinute;
        entity.MaxPromptCharacters = settings.MaxPromptCharacters;
        entity.RequestTimeoutSeconds = settings.RequestTimeoutSeconds;
        entity.CreatedAtUtc = settings.CreatedAtUtc;
        entity.UpdatedAtUtc = settings.UpdatedAtUtc;
        entity.LastSharedAtUtc = settings.LastSharedAtUtc;
    }

    private static LocalAiInstalledModel Map(LocalAiInstalledModelEntity entity) =>
        new(
            entity.ModelId,
            entity.DisplayName,
            entity.SizeBytes,
            entity.Digest,
            entity.ModifiedAtUtc,
            entity.IsRunning,
            entity.IsDefault,
            (AiProviderCapabilityFlag)entity.Capabilities,
            entity.Detail);

    private static LocalAiInstalledModelEntity Map(LocalAiInstalledModel model) =>
        new()
        {
            ModelId = model.ModelId,
            DisplayName = model.DisplayName,
            SizeBytes = model.SizeBytes,
            Digest = model.Digest,
            ModifiedAtUtc = model.ModifiedAtUtc,
            IsRunning = model.IsRunning,
            IsDefault = model.IsDefault,
            Capabilities = (int)model.Capabilities,
            Detail = model.Detail
        };

    private static LocalAiHardwareProfile Map(LocalAiHardwareSnapshotEntity entity) =>
        new(
            entity.CpuModel,
            entity.PhysicalCoreCount,
            entity.LogicalCoreCount,
            entity.TotalMemoryBytes,
            entity.AvailableMemoryBytes,
            entity.AvailableDiskBytes,
            (LocalAiGpuAccelerationState)entity.GpuAccelerationState,
            JsonSerializer.Deserialize<List<LocalAiGpuAdapter>>(entity.GpusJson, JsonOptions) ?? [],
            entity.Summary,
            entity.CapturedAtUtc);

    private static LocalAiHardwareSnapshotEntity Map(LocalAiHardwareProfile profile) =>
        new()
        {
            Id = Guid.NewGuid(),
            CpuModel = profile.CpuModel,
            PhysicalCoreCount = profile.PhysicalCoreCount,
            LogicalCoreCount = profile.LogicalCoreCount,
            TotalMemoryBytes = profile.TotalMemoryBytes,
            AvailableMemoryBytes = profile.AvailableMemoryBytes,
            AvailableDiskBytes = profile.AvailableDiskBytes,
            GpuAccelerationState = (int)profile.GpuAccelerationState,
            GpusJson = JsonSerializer.Serialize(profile.Gpus, JsonOptions),
            Summary = profile.Summary,
            CapturedAtUtc = profile.CapturedAtUtc
        };

    private static LocalAiBenchmarkResult Map(LocalAiBenchmarkResultEntity entity) =>
        new(
            entity.Id,
            entity.ModelId,
            entity.PromptSummary,
            entity.Succeeded,
            TimeSpan.FromMilliseconds(entity.DurationMilliseconds),
            entity.Detail,
            entity.ExecutedAtUtc);

    private static LocalAiBenchmarkResultEntity Map(LocalAiBenchmarkResult result) =>
        new()
        {
            Id = result.Id,
            ModelId = result.ModelId,
            PromptSummary = result.PromptSummary,
            Succeeded = result.Succeeded,
            DurationMilliseconds = (long)result.Duration.TotalMilliseconds,
            Detail = result.Detail,
            ExecutedAtUtc = result.ExecutedAtUtc
        };

    private static LocalAiUsageEntry Map(LocalAiUsageEntryEntity entity) =>
        new(
            entity.Id,
            entity.ProviderKey,
            (LocalAiUsageScope)entity.Scope,
            ParseGuidOrNull(entity.ConsumerOrganizationId),
            ParseGuidOrNull(entity.ConsumerInstanceId),
            entity.ConsumerDisplayName,
            entity.ModelId,
            entity.Succeeded,
            TimeSpan.FromMilliseconds(entity.DurationMilliseconds),
            entity.PromptCharacterCount,
            entity.OutputCharacterCount,
            entity.UsedToolCalls,
            entity.ResultSummary,
            entity.StartedAtUtc,
            entity.CompletedAtUtc);

    private static LocalAiUsageEntryEntity Map(LocalAiUsageEntry entry) =>
        new()
        {
            Id = entry.Id,
            ProviderKey = entry.ProviderKey,
            Scope = (int)entry.Scope,
            ConsumerOrganizationId = entry.ConsumerOrganizationId?.ToString("D"),
            ConsumerInstanceId = entry.ConsumerInstanceId?.ToString("D"),
            ConsumerDisplayName = entry.ConsumerDisplayName,
            ModelId = entry.ModelId,
            Succeeded = entry.Succeeded,
            DurationMilliseconds = (long)entry.Duration.TotalMilliseconds,
            PromptCharacterCount = entry.PromptCharacterCount,
            OutputCharacterCount = entry.OutputCharacterCount,
            UsedToolCalls = entry.UsedToolCalls,
            ResultSummary = entry.ResultSummary,
            StartedAtUtc = entry.StartedAtUtc,
            CompletedAtUtc = entry.CompletedAtUtc
        };

    private static LocalAiAuditEntry Map(LocalAiAuditEntryEntity entity) =>
        new(
            entity.Id,
            entity.EventType,
            entity.Scope,
            entity.Summary,
            entity.Detail,
            entity.Succeeded,
            entity.CreatedAtUtc);

    private static LocalAiAuditEntryEntity Map(LocalAiAuditEntry entry) =>
        new()
        {
            Id = entry.Id,
            EventType = entry.EventType,
            Scope = entry.Scope,
            Summary = entry.Summary,
            Detail = entry.Detail,
            Succeeded = entry.Succeeded,
            CreatedAtUtc = entry.CreatedAtUtc
        };

    private static IReadOnlyList<Guid> DeserializeGuids(string json) =>
        JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? [];

    private static IReadOnlyList<string> DeserializeStrings(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];

    private static Guid? ParseGuidOrNull(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;
}
