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
    Guid? RecipeRunId,
    string? PromptRecipeId,
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
    IReadOnlyList<string> Dependencies,
    bool HasCustomSettings = false);

public sealed record HomeLabConnectionGuide(
    Guid SourceInstallationId,
    string SourceName,
    IReadOnlyList<HomeLabConnectionAddress> Addresses);

public sealed record HomeLabConnectionAddress(
    Guid TargetInstallationId,
    string TargetAppId,
    string TargetName,
    string Purpose,
    string Host,
    int Port,
    string Url,
    bool IsReachable,
    string NetworkPath,
    string PortMapping,
    string Detail);

public sealed record HomeLabApplicationConfigInspection(
    Guid InstallationId,
    string Name,
    IReadOnlyList<HomeLabApplicationConfigFile> Files,
    string Detail);

public sealed record HomeLabApplicationConfigFile(
    string RelativePath,
    string Sha256,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string RedactedContent);

public sealed record HomeLabApplicationConfigPatch(
    string RelativePath,
    string ExpectedSha256,
    string ExpectedText,
    string ReplacementText);

public sealed record HomeLabApplicationConfigRepairResult(
    bool Succeeded,
    bool RolledBack,
    string Name,
    string RelativePath,
    string BackupRelativePath,
    string Detail,
    HomeLabHealthState HealthState,
    DateTimeOffset CompletedAtUtc);

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
