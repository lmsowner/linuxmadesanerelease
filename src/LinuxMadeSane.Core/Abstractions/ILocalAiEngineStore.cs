// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalAiEngineStore
{
    Task<LocalAiEngineSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(LocalAiEngineSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAiInstalledModel>> ListInstalledModelsAsync(CancellationToken cancellationToken = default);
    Task ReplaceInstalledModelsAsync(IReadOnlyList<LocalAiInstalledModel> models, CancellationToken cancellationToken = default);
    Task<LocalAiHardwareProfile?> GetLatestHardwareProfileAsync(CancellationToken cancellationToken = default);
    Task SaveHardwareProfileAsync(LocalAiHardwareProfile profile, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAiBenchmarkResult>> ListBenchmarkResultsAsync(CancellationToken cancellationToken = default);
    Task SaveBenchmarkResultAsync(LocalAiBenchmarkResult result, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAiUsageEntry>> ListUsageAsync(CancellationToken cancellationToken = default);
    Task SaveUsageAsync(LocalAiUsageEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAiAuditEntry>> ListAuditEntriesAsync(CancellationToken cancellationToken = default);
    Task SaveAuditEntryAsync(LocalAiAuditEntry entry, CancellationToken cancellationToken = default);
}
