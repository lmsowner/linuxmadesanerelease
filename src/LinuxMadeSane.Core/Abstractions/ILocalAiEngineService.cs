// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalAiEngineService
{
    Task<LocalAiEngineStatus> InspectAsync(CancellationToken cancellationToken = default);
    Task<LocalAiSetupPlan> BuildSetupPlanAsync(
        string selectedModelId,
        bool enableSharing,
        bool dryRun,
        CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> ApplySetupPlanAsync(
        string selectedModelId,
        bool enableSharing,
        bool approved,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAiModelDefinition>> ListRecommendedModelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAiInstalledModel>> RefreshInstalledModelsAsync(CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> PullModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> RemoveModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiBenchmarkResult> TestModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task<LocalAiBenchmarkResult> BenchmarkAsync(string modelId, CancellationToken cancellationToken = default);
    Task<LocalAiEngineSettings> SaveSharingSettingsAsync(LocalAiEngineSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteLmsAiEngineDescriptor>> DiscoverSharedEnginesAsync(CancellationToken cancellationToken = default);
    Task<AiProviderSettings> CreateOrUpdateLocalProviderAsync(string modelId, CancellationToken cancellationToken = default);
    Task<AiProviderSettings> CreateOrUpdateRemoteProviderAsync(
        string displayName,
        string modelId,
        RemoteLmsAiEngineReference reference,
        CancellationToken cancellationToken = default);
}
