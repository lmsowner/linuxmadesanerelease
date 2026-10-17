// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayDashboardViewModel(
    EdgeGatewaySettingsViewModel Settings,
    IReadOnlyList<EdgeGatewayRouteListItem> Routes,
    IReadOnlyList<EdgeGatewayAuditEntry> AuditEntries,
    EdgeGatewayCloudflareStatus Cloudflare,
    EdgeGatewayRuntimeStatus Runtime,
    string GeneratedCaddyfile,
    string CloudflareTunnelSnippet,
    string CloudflareDnsTargetHint);
