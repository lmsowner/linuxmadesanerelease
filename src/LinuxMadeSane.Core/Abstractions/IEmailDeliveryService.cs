// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Abstractions;

public interface IEmailDeliveryService
{
    Task<EmailDeliveryResult> SendHtmlAsync(
        string recipientAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);
}

public sealed record EmailDeliveryResult(
    bool Succeeded,
    bool Attempted,
    string Message);
