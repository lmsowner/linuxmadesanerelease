// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
