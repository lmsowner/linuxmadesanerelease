// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface IDockerAiEngineDiscoveryService
{
    IReadOnlyList<DockerAiEngineCatalogItem> ListCatalog();
    Task<DockerAiEngineWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> InstallEngineAsync(string engineId, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> StartContainerAsync(string containerIdOrName, bool approved, CancellationToken cancellationToken = default);
    Task<LocalAiApplyResult> StopContainerAsync(string containerIdOrName, bool approved, CancellationToken cancellationToken = default);
}
