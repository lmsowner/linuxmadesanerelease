// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services;

internal static partial class ManagedMailRelaySmtpClient
{
    public static async Task<ManagedMailRelaySmtpSubmission> SendAsync(
        MailRelayConfiguration configuration,
        string username,
        string password,
        MailAddress sender,
        MailAddress recipient,
        string subject,
        string body,
        string contentType,
        string certificatePem,
        CancellationToken cancellationToken)
    {
        using var expectedCertificate = X509Certificate2.CreateFromPem(certificatePem);
        var expectedCertificateHash = SHA256.HashData(expectedCertificate.RawData);
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, configuration.SubmissionPort, cancellationToken);
        await using var networkStream = tcpClient.GetStream();
        using var initialReader = new StreamReader(networkStream, Encoding.ASCII, false, leaveOpen: true);

        var reply = await ReadReplyAsync(initialReader, cancellationToken);
        RequireCode(reply, 220, "SMTP greeting");
        reply = await SendCommandAsync(networkStream, initialReader, $"EHLO {configuration.RelayHostname}\r\n", cancellationToken);
        RequireCode(reply, 250, "EHLO");
        if (!reply.Lines.Any(line => line.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The relay did not advertise STARTTLS.");
        }

        reply = await SendCommandAsync(networkStream, initialReader, "STARTTLS\r\n", cancellationToken);
        RequireCode(reply, 220, "STARTTLS");

        await using var tlsStream = new SslStream(
            networkStream,
            leaveInnerStreamOpen: true,
            (_, certificate, _, _) => CertificateMatches(certificate, expectedCertificateHash));
        await tlsStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = configuration.RelayHostname,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            cancellationToken);

        using var tlsReader = new StreamReader(tlsStream, Encoding.ASCII, false, leaveOpen: true);
        reply = await SendCommandAsync(tlsStream, tlsReader, $"EHLO {configuration.RelayHostname}\r\n", cancellationToken);
        RequireCode(reply, 250, "secure EHLO");
        if (!reply.Lines.Any(line => line.Contains("AUTH", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The relay did not advertise SMTP AUTH after STARTTLS.");
        }

        var authPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{username}\0{password}"));
        reply = await SendCommandAsync(tlsStream, tlsReader, $"AUTH PLAIN {authPayload}\r\n", cancellationToken);
        if (reply.Code != 235)
        {
            return new ManagedMailRelaySmtpSubmission(false, false, null, "authentication", reply.Text);
        }

        reply = await SendCommandAsync(tlsStream, tlsReader, $"MAIL FROM:<{sender.Address}>\r\n", cancellationToken);
        if (reply.Code != 250)
        {
            return new ManagedMailRelaySmtpSubmission(true, false, null, "sender", reply.Text);
        }

        reply = await SendCommandAsync(tlsStream, tlsReader, $"RCPT TO:<{recipient.Address}>\r\n", cancellationToken);
        if (reply.Code is not (250 or 251))
        {
            return new ManagedMailRelaySmtpSubmission(true, false, null, "recipient", reply.Text);
        }

        reply = await SendCommandAsync(tlsStream, tlsReader, "DATA\r\n", cancellationToken);
        if (reply.Code != 354)
        {
            return new ManagedMailRelaySmtpSubmission(true, false, null, "data", reply.Text);
        }

        var message = BuildMessage(configuration, sender, recipient, subject, body, contentType);
        reply = await SendCommandAsync(tlsStream, tlsReader, message + "\r\n.\r\n", cancellationToken);
        if (reply.Code != 250)
        {
            return new ManagedMailRelaySmtpSubmission(true, false, null, "message", reply.Text);
        }

        var queueMatch = QueueIdRegex().Match(reply.Text);
        var queueId = queueMatch.Success ? queueMatch.Groups["id"].Value : null;
        var acceptedResponse = reply.Text;
        try
        {
            await SendCommandAsync(tlsStream, tlsReader, "QUIT\r\n", cancellationToken);
        }
        catch (IOException)
        {
            // The message has already been accepted. QUIT is best effort only.
        }

        return new ManagedMailRelaySmtpSubmission(true, true, queueId, "accepted", acceptedResponse);
    }

    private static string BuildMessage(
        MailRelayConfiguration configuration,
        MailAddress sender,
        MailAddress recipient,
        string subject,
        string body,
        string contentType)
    {
        var now = DateTimeOffset.UtcNow;
        var encodedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(body ?? string.Empty));
        var bodyLines = Enumerable.Range(0, (encodedBody.Length + 75) / 76)
            .Select(index => encodedBody.Substring(index * 76, Math.Min(76, encodedBody.Length - (index * 76))));
        return string.Join("\r\n",
        [
            $"Date: {now.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)}",
            $"Message-ID: <{Guid.NewGuid():N}@{configuration.RelayHostname}>",
            $"From: {CleanHeaderValue(sender.ToString())}",
            $"To: {recipient.Address}",
            $"Subject: {CleanHeaderValue(subject)}",
            "MIME-Version: 1.0",
            $"Content-Type: {CleanContentType(contentType)}; charset=utf-8",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            .. bodyLines
        ]);
    }

    private static string CleanHeaderValue(string value) =>
        string.Join(' ', (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string CleanContentType(string value) =>
        value.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ? "text/plain" : "text/html";

    private static bool CertificateMatches(X509Certificate? certificate, byte[] expectedHash)
    {
        if (certificate is null)
        {
            return false;
        }

        var actualHash = SHA256.HashData(certificate.GetRawCertData());
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static async Task<SmtpReply> SendCommandAsync(
        Stream stream,
        StreamReader reader,
        string command,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(command), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return await ReadReplyAsync(reader, cancellationToken);
    }

    private static async Task<SmtpReply> ReadReplyAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        int? responseCode = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The SMTP service closed the connection.");
            lines.Add(line);
            if (line.Length < 3 || !int.TryParse(line[..3], out var code))
            {
                throw new IOException($"The SMTP service returned an invalid response: {CleanHeaderValue(line)}");
            }

            responseCode ??= code;
            if (line.Length == 3 || line[3] == ' ')
            {
                return new SmtpReply(responseCode.Value, lines, CleanHeaderValue(string.Join(" | ", lines)));
            }
        }
    }

    private static void RequireCode(SmtpReply reply, int expectedCode, string stage)
    {
        if (reply.Code != expectedCode)
        {
            throw new InvalidOperationException($"{stage} failed: {reply.Text}");
        }
    }

    private sealed record SmtpReply(int Code, IReadOnlyList<string> Lines, string Text);

    [GeneratedRegex(@"queued as\s+(?<id>[A-F0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueueIdRegex();
}

internal sealed record ManagedMailRelaySmtpSubmission(
    bool Authenticated,
    bool Accepted,
    string? QueueId,
    string Stage,
    string Response);
