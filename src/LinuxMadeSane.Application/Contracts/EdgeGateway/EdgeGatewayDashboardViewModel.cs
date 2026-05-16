// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
