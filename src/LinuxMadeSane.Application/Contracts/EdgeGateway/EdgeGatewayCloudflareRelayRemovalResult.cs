// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflareRelayRemovalResult(
    bool Success,
    bool RequiresConfirmation,
    string DomainName,
    string GatewayDomainName,
    string WildcardHostname,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);
