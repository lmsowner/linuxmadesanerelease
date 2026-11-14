// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflareRouteSetupResult(
    bool Success,
    bool RequiresDnsReplacement,
    Guid RouteId,
    string Hostname,
    string RelayHostname,
    string DnsTarget,
    string TunnelName,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);
