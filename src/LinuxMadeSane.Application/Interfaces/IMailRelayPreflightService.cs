// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMailRelayPreflightService
{
    Task<MailRelayPublicIpv4DetectionResult> DetectPublicIpv4Async(
        CancellationToken cancellationToken = default);

    Task<MailRelayPreflightResult> InspectAsync(
        bool verifyDnsEdit,
        string? cloudflareZoneId = null,
        CancellationToken cancellationToken = default);
}
