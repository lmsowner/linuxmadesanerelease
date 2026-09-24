// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record OnDemandAppsAvailability(
    bool IsAvailable,
    string Message,
    string PublishedHostname = "",
    string GatewayDomainName = "",
    string DomainName = "");

public sealed record OnDemandAppLaunch(
    Guid LeaseId,
    string Hostname,
    string Url,
    DateTimeOffset StaleAfterUtc);

public sealed record OnDemandAppLaunchProgress(string Message);
