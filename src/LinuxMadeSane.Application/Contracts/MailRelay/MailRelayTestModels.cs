// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.MailRelay;

public enum MailRelayTestStatus
{
    Failed = 0,
    Rejected = 1,
    Queued = 2,
    Sent = 3,
    Deferred = 4,
    Bounced = 5
}

public sealed record MailRelayTestRequest(
    Guid ClientId,
    string FromAddress,
    string RecipientAddress);

public sealed record MailRelayTestResult(
    MailRelayTestStatus Status,
    bool ClientPolicyValidated,
    bool AcceptedByRelay,
    string ApplicationName,
    string FromAddress,
    string RecipientAddress,
    string? QueueId,
    string? DestinationServer,
    string SmtpResponse,
    string Summary,
    DateTimeOffset TestedAtUtc,
    bool DmarcIdentityAligned = false)
{
    public bool Passed =>
        ClientPolicyValidated &&
        AcceptedByRelay &&
        Status is MailRelayTestStatus.Queued or MailRelayTestStatus.Sent;
}
