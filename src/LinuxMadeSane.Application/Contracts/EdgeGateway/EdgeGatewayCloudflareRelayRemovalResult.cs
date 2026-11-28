// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
