// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflareDomainOption(
    string ZoneId,
    string DomainName,
    string GatewayDomainName,
    string AccountId,
    string AccountName,
    string Status,
    bool Paused,
    bool IsSavedDefault,
    bool RelayConfigured,
    string RelayDnsTarget,
    bool RelayUsesCloudflareTunnel,
    string RelayTunnelId,
    string RelayTunnelName,
    bool RelayOwnedByThisLms,
    string RelayOwnershipSummary);
