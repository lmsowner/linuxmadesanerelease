// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRuntimeStatus(
    EdgeGatewayRuntimeComponentStatus Caddy,
    EdgeGatewayRuntimeComponentStatus Cloudflared,
    bool CanAttemptPublish,
    string Summary);
