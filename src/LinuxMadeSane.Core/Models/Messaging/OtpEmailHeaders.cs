// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Globalization;
using System.Net.Mail;
using System.Text;

namespace LinuxMadeSane.Core.Models.Messaging;

public static class OtpEmailHeaders
{
    public static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000";

    // Graph's MIME send path carries the standard Expires header (JSON custom headers
    // are restricted to X- headers). Keep HTML and all user-provided header text encoded.
    public static string BuildMime(string senderAddress, string senderName, string recipientAddress,
        string subject, string html, DateTimeOffset expiresAtUtc)
    {
        var sender = new MailAddress(senderAddress);
        var recipient = new MailAddress(recipientAddress);
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        var lines = Enumerable.Range(0, (body.Length + 75) / 76)
            .Select(i => body.Substring(i * 76, Math.Min(76, body.Length - i * 76)));
        static string Encoded(string value) => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";
        return string.Join("\r\n", new[] {
            "Date: " + FormatDate(DateTimeOffset.UtcNow),
            "Expires: " + FormatDate(expiresAtUtc),
            $"Message-ID: <{Guid.NewGuid():N}@{sender.Host}>",
            $"From: {Encoded(senderName)} <{sender.Address}>",
            $"To: <{recipient.Address}>",
            "Subject: " + Encoded(subject),
            "MIME-Version: 1.0", "Content-Type: text/html; charset=utf-8",
            "Content-Transfer-Encoding: base64", "", string.Join("\r\n", lines), "" });
    }
}
