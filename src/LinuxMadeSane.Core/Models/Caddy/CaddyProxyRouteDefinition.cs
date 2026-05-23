// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Caddy;

public sealed record CaddyProxyRouteDefinition(
    Guid Id,
    string Name,
    string Hostname,
    string UpstreamUrl,
    string Description,
    bool EnableTls,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CaddyProxyRouteKind Kind = CaddyProxyRouteKind.HostnameReverseProxy,
    string SourceIp = "",
    int SourcePort = 0,
    string DestinationIp = "",
    int DestinationPort = 0,
    CaddyProxyTargetScheme DestinationScheme = CaddyProxyTargetScheme.Http);
