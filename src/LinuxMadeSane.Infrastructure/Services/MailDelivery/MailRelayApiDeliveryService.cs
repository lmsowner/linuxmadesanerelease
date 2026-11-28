// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class MailRelayApiDeliveryService(IMailRelayStore relay, IMailDeliveryReceipts receipts, IOutboundMailProvider provider)
{
    public async Task<MailDeliveryResult> DeliverAsync(OutboundMailMessage message, CancellationToken token)
    {
        var configuration = await relay.GetConfigurationAsync(token);
        if (configuration?.Enabled != true || !configuration.Outbound.UsesHttpsApi)
            return new(MailDeliveryOutcome.ConfigurationFailure, "HTTPS delivery is not enabled. Review the relay delivery settings.");
        if (message.QueueId.Length is < 1 or > 100 || message.QueueId.Any(c => !char.IsAsciiLetterOrDigit(c)) || message.Recipients.Length is < 1 or > 50 ||
            message.Recipients.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 320 || r.Any(char.IsControl)) || message.EnvelopeSender.Length > 320 || message.EnvelopeSender.Any(char.IsControl))
            return new(MailDeliveryOutcome.ConfigurationFailure, "Invalid native queue envelope.");
        var max = configuration.Outbound.Mode == MailRelayOutboundMode.MicrosoftGraph ? 2_800_000 : 26_214_400;
        if (message.Mime.Length > max) return new(MailDeliveryOutcome.PermanentFailure, $"Message exceeds this provider's {max / 1_000_000.0:0.#} MB MIME submission limit, including attachment encoding.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(message.Mime));
        var recipientKeys = message.Recipients.Distinct(StringComparer.Ordinal).Select(recipient =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.EnvelopeSender + "\n" + recipient + "\n" + fingerprint)))).Order(StringComparer.Ordinal).ToArray();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.QueueId + "\n" + string.Join("\n", recipientKeys))));
        var claim = await receipts.ClaimAsync(key, message.QueueId, token, recipientKeys);
        if (!claim.Claimed) return new(claim.Receipt.Outcome, claim.Receipt.Detail, claim.Receipt.ProviderMessageId, claim.Receipt.RetryAfterUtc);
        MailDeliveryResult result;
        try { result = await provider.SendAsync(configuration.Outbound, message, token); }
        catch { result = new(MailDeliveryOutcome.Indeterminate, "Submission ended without a confirmed result. Check provider tracing before resolving this attempt in LMS."); }
        await receipts.CompleteAsync(claim.Receipt, result, CancellationToken.None);
        return result;
    }
}
