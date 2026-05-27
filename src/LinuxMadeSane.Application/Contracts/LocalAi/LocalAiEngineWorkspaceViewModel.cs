// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Application.Contracts.LocalAi;

public sealed record LocalAiEngineWorkspaceViewModel(
    LocalAiEngineStatus Status,
    IReadOnlyList<LocalAiModelDefinition> RecommendedModels,
    IReadOnlyList<LocalAiInstalledModel> InstalledModels,
    IReadOnlyList<LocalAiBenchmarkResult> Benchmarks,
    IReadOnlyList<LocalAiUsageEntry> UsageEntries,
    IReadOnlyList<LocalAiAuditEntry> AuditEntries,
    DockerAiEngineWorkspace DockerEngines,
    IReadOnlyList<RemoteLmsAiEngineDescriptor> RemoteEngines,
    bool IsPortalConnected);
