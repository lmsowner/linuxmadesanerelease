// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class HomeLabServiceEndpointEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public string ServiceId { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public int Scope { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Scheme { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string PathBase { get; set; } = string.Empty;
    public int? RoutingMode { get; set; }
    public Guid? EdgeGatewayRouteId { get; set; }
    public int HealthState { get; set; }
    public string HealthDetail { get; set; } = string.Empty;
    public DateTimeOffset? CheckedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public HomeLabInstallationEntity? Installation { get; set; }
}
