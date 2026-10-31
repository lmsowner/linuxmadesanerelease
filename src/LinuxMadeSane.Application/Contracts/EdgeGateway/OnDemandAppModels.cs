// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
