// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
