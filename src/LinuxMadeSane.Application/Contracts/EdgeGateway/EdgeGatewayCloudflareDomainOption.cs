// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
