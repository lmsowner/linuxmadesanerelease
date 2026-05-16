// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Caddy;

public sealed record CaddyProxyRouteDefinition(
    Guid Id,
    string Name,
    string Hostname,
    string UpstreamUrl,
    string Description,
    bool EnableTls,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
