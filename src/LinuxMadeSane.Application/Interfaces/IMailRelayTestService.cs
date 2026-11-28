// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Application.Interfaces;

public interface IMailRelayTestService
{
    Task<MailRelayTestResult> SendAsync(
        MailRelayTestRequest request,
        CancellationToken cancellationToken = default);
}
