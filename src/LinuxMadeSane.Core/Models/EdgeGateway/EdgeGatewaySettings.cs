// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewaySettings(
    int Id,
    string GatewaySubdomain,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
