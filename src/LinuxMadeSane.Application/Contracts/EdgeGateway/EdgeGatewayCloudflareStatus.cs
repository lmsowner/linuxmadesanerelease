// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflareStatus(
    bool HasSavedToken,
    bool IsAvailable,
    string Message,
    IReadOnlyList<EdgeGatewayCloudflareDomainOption> Domains);
