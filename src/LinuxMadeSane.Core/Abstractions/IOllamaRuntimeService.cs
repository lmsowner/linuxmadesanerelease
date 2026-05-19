// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface IOllamaRuntimeService
{
    Task<LocalAiRuntime> InspectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAiInstalledModel>> ListInstalledModelsAsync(CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> InstallAsync(bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> StartAsync(bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> StopAsync(bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> RestartAsync(bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> PullModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> RemoveModelAsync(string modelId, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiBenchmarkResult> TestModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task<AiProviderTurnResult> ExecuteAsync(
        string modelId,
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default);
}
