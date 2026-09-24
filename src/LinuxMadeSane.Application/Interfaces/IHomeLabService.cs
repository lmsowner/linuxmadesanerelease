// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Application.Interfaces;

public interface IHomeLabService
{
    Task<HomeLabWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default);
    Task<HomeLabWorkspace> GetWorkspaceSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetWorkspaceAsync(cancellationToken);
    Task<IReadOnlyList<HomeLabRuntimeHealth>> RefreshRuntimeHealthAsync(CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<HomeLabServiceEndpoint>> RefreshEndpointHealthAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceSnapshotAsync(cancellationToken);
        return workspace.Installations.FirstOrDefault(item => item.Id == installationId)?.ServiceEndpoints ?? [];
    }
    Task<HomeLabStorageRole> SaveStorageRoleAsync(string role, string hostPath, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> ApplyStandardStorageAsync(CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> InstallAppAsync(HomeLabInstallRequest request, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> InstallRecipeAsync(HomeLabRecipeInstallRequest request, CancellationToken cancellationToken = default);
    Task AssignRecipeRunAsync(HomeLabRecipeRunAssignment assignment, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> ReconfigureVpnGatewayAsync(Guid installationId, IReadOnlyDictionary<string, string> configuration, IReadOnlyDictionary<string, string> secretConfiguration, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> SetNetworkRouteAsync(Guid installationId, bool useVpnGateway, Guid? gatewayInstallationId = null, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> UpdateContainerSettingsAsync(Guid installationId, HomeLabContainerSettingsUpdate settings, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> RestoreContainerSettingsAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<HomeLabNetworkSecurity> GetNetworkSecurityAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> ExecuteAsync(Guid installationId, HomeLabLifecycleAction action, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> ResetCredentialsAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<HomeLabLogsResult> GetLogsAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<HomeLabEffectiveConfiguration?> GetEffectiveConfigurationAsync(Guid deploymentId, CancellationToken cancellationToken = default);
    Task<HomeLabConnectionGuide> GetConnectionGuideAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> PublishExternalAccessAsync(Guid installationId, HomeLabEdgeGatewayRequest request, CancellationToken cancellationToken = default);
    Task<HomeLabOperationResult> UnpublishExternalAccessAsync(Guid installationId, CancellationToken cancellationToken = default);
    Task<HomeLabApplicationConfigInspection> InspectApplicationConfigAsync(Guid installationId, string? relativePath = null, CancellationToken cancellationToken = default);
    Task<HomeLabApplicationConfigRepairResult> RepairApplicationConfigAsync(Guid installationId, HomeLabApplicationConfigPatch patch, CancellationToken cancellationToken = default);
}
