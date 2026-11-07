// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Abstractions;

public interface IEmailDeliveryService
{
    // Only disposable sign-in challenges use this path; setup and alert mail remain ordinary mail.
    Task<EmailDeliveryResult> SendOtpHtmlAsync(string recipientAddress, string subject, string htmlBody,
        CancellationToken cancellationToken = default) => SendHtmlAsync(recipientAddress, subject, htmlBody, cancellationToken);

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
