// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using MimeKit;
using System.Text;
using LinuxMadeSane.Application.Contracts.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class MailRelayMime
{
    internal static byte[] Prepare(OutboundMailMessage message, string sender, bool headersAreEnvelope)
    {
        // Parse only headers. Keep MIME body/attachment bytes exactly as queued.
        var separator = message.Mime.AsSpan().IndexOf("\r\n\r\n"u8);
        if (separator < 0 || separator > 128 * 1024) throw new InvalidDataException("Message has invalid or oversized MIME headers.");
        var unfolded = Encoding.UTF8.GetString(message.Mime, 0, separator).Replace("\r\n\t", " ").Replace("\r\n ", " ").Split("\r\n");
        var from = unfolded.Where(h => h.StartsWith("From:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (from.Length != 1 || !MailboxAddress.TryParse(from[0][5..].Trim(), out var address) || !address.Address.Equals(sender, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("From address is not the authorised provider mailbox. Use that mailbox as From and put another response address in Reply-To.");
        foreach (var header in unfolded.Where(h => h.StartsWith("Expires:", StringComparison.OrdinalIgnoreCase)))
            if (DateTimeOffset.TryParse(header[8..], out var expires) && expires <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("Message expired before submission; it has not been sent.");
        if (!headersAreEnvelope) return message.Mime;
        var recipients = message.Recipients.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projected = new List<string>();
        foreach (var name in new[] { "To", "Cc" })
        {
            foreach (var header in unfolded.Where(h => h.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (!InternetAddressList.TryParse(header[(name.Length + 1)..], out var list)) throw new InvalidDataException("Message has an invalid recipient header.");
                    var mailboxes = list.Mailboxes.ToArray();
                    var included = mailboxes.Where(a => recipients.Contains(a.Address)).ToArray();
                    foreach (var includedAddress in included) visible.Add(includedAddress.Address);
                    if (included.Length == mailboxes.Length) projected.Add(header);
                    else if (included.Length > 0) projected.Add(name + ": " + new InternetAddressList(included).ToString(FormatOptions.Default, true));
                }
                catch (FormatException) { throw new InvalidDataException("Message has an invalid recipient header."); }
            }
        }
        // Graph/Gmail use these headers as their envelope. Preserve full To/Cc
        // lists for ordinary batches, remove header-only recipients, and include
        // blind envelope recipients without exposing them in visible headers.
        var blind = recipients.Where(r => !visible.Contains(r)).ToArray();
        if (blind.Length > 0) projected.Add("Bcc: " + string.Join(", ", blind.Select(r => "<" + r + ">")));
        var removed = new[] { "To:", "Cc:", "Bcc:", "Resent-To:", "Resent-Cc:", "Resent-Bcc:", "DKIM-Signature:", "ARC-Seal:", "ARC-Message-Signature:", "ARC-Authentication-Results:" };
        var headers = unfolded.Where(h => !removed.Any(n => h.StartsWith(n, StringComparison.OrdinalIgnoreCase))).Concat(projected);
        var prefix = Encoding.UTF8.GetBytes(string.Join("\r\n", headers) + "\r\n\r\n");
        var result = new byte[prefix.Length + message.Mime.Length - separator - 4];
        prefix.CopyTo(result, 0); message.Mime.AsSpan(separator + 4).CopyTo(result.AsSpan(prefix.Length));
        return result;
    }
}
