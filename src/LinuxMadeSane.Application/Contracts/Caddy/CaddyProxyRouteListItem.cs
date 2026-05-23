// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Caddy;

public sealed record CaddyProxyRouteListItem(
    Guid Id,
    CaddyProxyRouteKind Kind,
    string KindLabel,
    string Name,
    string Hostname,
    string UpstreamUrl,
    string Description,
    bool EnableTls,
    string AddressLabel,
    string TargetLabel,
    string GeneratedSnippet,
    IReadOnlyList<string> Warnings,
    DateTimeOffset UpdatedAtUtc);
