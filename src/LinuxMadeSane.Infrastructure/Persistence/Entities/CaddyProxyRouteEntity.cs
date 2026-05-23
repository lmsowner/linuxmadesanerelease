// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class CaddyProxyRouteEntity
{
    public Guid Id { get; set; }
    public int RouteKind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string UpstreamUrl { get; set; } = string.Empty;
    public string SourceIp { get; set; } = string.Empty;
    public int SourcePort { get; set; }
    public string DestinationIp { get; set; } = string.Empty;
    public int DestinationPort { get; set; }
    public int DestinationScheme { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool EnableTls { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
