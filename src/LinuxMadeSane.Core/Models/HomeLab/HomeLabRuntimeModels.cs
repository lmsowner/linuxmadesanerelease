// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.HomeLab;

public sealed record HomeLabStorageRole(
    string Role,
    string HostPath,
    DateTimeOffset UpdatedAtUtc);

public sealed record HomeLabAppInstallation(
    Guid Id,
    Guid DeploymentId,
    string AppId,
    string DisplayName,
    string ContainerName,
    string NetworkName,
    string Image,
    string VolumeMappingsJson,
    string PortMappingsJson,
    string NetworkMode,
    Guid? EdgeGatewayRouteId,
    Guid? CaddyRouteId,
    int? CaddySourcePort,
    HomeLabHealthState HealthState,
    string HealthDetail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsRecipeInstallation);

public sealed record HomeLabDeployment(
    Guid Id,
    string Name,
    string? RecipeId,
    string NetworkName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record HomeLabWorkspace(
    IReadOnlyList<HomeLabAppManifest> Apps,
    IReadOnlyList<HomeLabRecipeManifest> Recipes,
    IReadOnlyList<HomeLabStorageRole> StorageRoles,
    IReadOnlyList<HomeLabDeployment> Deployments,
    IReadOnlyList<HomeLabAppInstallation> Installations);

public sealed record HomeLabEffectiveConfiguration(
    Guid DeploymentId,
    string NetworkName,
    IReadOnlyList<HomeLabEffectiveContainer> Containers);

public sealed record HomeLabEffectiveContainer(
    string AppId,
    string ContainerName,
    string Image,
    string NetworkMode,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Access,
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> Connections,
    IReadOnlyList<string> Dependencies);

public sealed record HomeLabNetworkSecurity(
    string Status,
    bool IsVpnRouted,
    bool IsSecured,
    string Route,
    string GatewayContainer,
    string GatewayHealth,
    string? PublicIp,
    string Detail,
    DateTimeOffset CheckedAtUtc,
    string? PortForwardingStatus = null,
    int? ForwardedPort = null,
    string? PortForwardingDetail = null);
