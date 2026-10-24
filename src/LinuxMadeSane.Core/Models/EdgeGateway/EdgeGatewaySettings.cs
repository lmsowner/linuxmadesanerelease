// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewaySettings(
    int Id,
    string GatewaySubdomain,
    string TunnelInstanceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
