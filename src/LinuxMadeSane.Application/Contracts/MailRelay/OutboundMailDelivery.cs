// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Application.Contracts.MailRelay;

public enum MailDeliveryOutcome { Accepted, TemporaryFailure, PermanentFailure, AuthenticationFailure, ConfigurationFailure, Indeterminate }
public sealed record MailDeliveryResult(MailDeliveryOutcome Outcome, string Detail, string? ProviderMessageId = null, DateTimeOffset? RetryAfterUtc = null);
// The SMTP envelope is authoritative. Header-only recipients must never receive a copy.
public sealed record OutboundMailMessage(string QueueId, string EnvelopeSender, string[] Recipients, byte[] Mime);
public sealed record MailDeliveryReceipt(string Key, string QueueId, MailDeliveryOutcome Outcome, string Detail, string? ProviderMessageId, DateTimeOffset UpdatedUtc, DateTimeOffset? RetryAfterUtc)
{
    public string[] EnvelopeKeys { get; init; } = [];
    public bool ManualRetryApproved { get; init; }
}

public interface IOutboundMailProvider
{
    Task<MailDeliveryResult> SendAsync(MailRelayOutboundSettings settings, OutboundMailMessage message, CancellationToken cancellationToken = default);
    Task<MailDeliveryResult> CheckAsync(MailRelayOutboundSettings settings, CancellationToken cancellationToken = default);
}
public interface IMailDeliveryReceipts
{
    Task<(bool Claimed, MailDeliveryReceipt Receipt)> ClaimAsync(string key, string queueId, CancellationToken cancellationToken, string[]? envelopeKeys = null);
    Task CompleteAsync(MailDeliveryReceipt receipt, MailDeliveryResult result, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailDeliveryReceipt>> ListAttentionAsync(CancellationToken cancellationToken = default);
    Task ResolveAsync(string key, bool providerAccepted, CancellationToken cancellationToken = default);
}
