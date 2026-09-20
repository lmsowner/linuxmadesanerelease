// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class HomeLabInstallationEntity
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string VolumeMappingsJson { get; set; } = "[]";
    public string PortMappingsJson { get; set; } = "[]";
    public string ConfigurationJson { get; set; } = "{}";
    public Guid? EdgeGatewayRouteId { get; set; }
    public int HealthState { get; set; }
    public string HealthDetail { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public bool IsRecipeInstallation { get; set; }
    public HomeLabDeploymentEntity? Deployment { get; set; }
}
