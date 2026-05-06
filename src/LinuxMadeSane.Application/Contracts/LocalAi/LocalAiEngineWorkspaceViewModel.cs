using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Application.Contracts.LocalAi;

public sealed record LocalAiEngineWorkspaceViewModel(
    LocalAiEngineStatus Status,
    IReadOnlyList<LocalAiModelDefinition> RecommendedModels,
    IReadOnlyList<LocalAiInstalledModel> InstalledModels,
    IReadOnlyList<LocalAiBenchmarkResult> Benchmarks,
    IReadOnlyList<LocalAiUsageEntry> UsageEntries,
    IReadOnlyList<LocalAiAuditEntry> AuditEntries,
    IReadOnlyList<RemoteLmsAiEngineDescriptor> RemoteEngines,
    bool IsPortalConnected);
