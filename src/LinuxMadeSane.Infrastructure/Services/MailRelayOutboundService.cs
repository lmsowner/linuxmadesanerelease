// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services;

public enum SmtpSubmissionProbeStatus { Verified, Unreachable, Rejected }
public interface ISmtpSubmissionProbe
{
    Task<SmtpSubmissionProbeStatus> CheckAsync(string host, string username, string password, CancellationToken cancellationToken);
}

public sealed class MailRelayOutboundService(ISecretStore secrets, ISmtpSubmissionProbe probe) : IMailRelayOutboundService
{
    public async Task<MailRelayOutboundInspection> InspectAsync(MailRelayOutboundSettings settings, MailRelayPreflightResult preflight, CancellationToken cancellationToken = default)
    {
        if (settings.UsesHttpsApi)
        {
            var error = HttpsOutboundMailProvider.Validate(settings);
            var apiCheck = error is null
                ? new MailRelayPreflightCheck(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound delivery", MailRelayPreflightCheckState.Pass,
                    "HTTPS 443", $"{settings.Mode} over HTTPS. Provider owns sending identity and DKIM; send a test to validate authorisation.")
                : Failed(error);
            return new(preflight with { HttpsDelivery = true, Checks = preflight.Checks.Select(c => c.Key == apiCheck.Key ? apiCheck : c).ToArray() }, false, apiCheck.Detail) { DestinationPort = 443 };
        }
        var direct = preflight.GetCheck(MailRelayPreflightCheckKeys.OutboundSmtp);
        var useUpstream = false;
        var destinationPort = 25;
        var check = direct;
        var summary = "Direct delivery to recipient mail servers on TCP/25 (opportunistic TLS).";
        var validation = Validate(settings);
        if (validation is not null) check = Failed(validation);
        else if (settings.Mode is MailRelayOutboundMode.DirectPort587 or MailRelayOutboundMode.AutomaticDirect)
        {
            destinationPort = settings.Mode == MailRelayOutboundMode.DirectPort587 || direct.State != MailRelayPreflightCheckState.Pass ? 587 : 25;
            if (destinationPort == 587)
            {
                summary = "Direct DNS/MX delivery on TCP/587, without an external relay or SMTP login. Destination acceptance has not been verified; messages queue if a recipient MX does not accept this port.";
                check = new(direct.Key, "Outbound delivery", MailRelayPreflightCheckState.Warning, "DIRECT 587 · UNTESTED", summary);
            }
        }
        else if (settings.Mode != MailRelayOutboundMode.DirectPort25)
        {
            if (string.IsNullOrWhiteSpace(settings.Host))
            {
                if (settings.Mode == MailRelayOutboundMode.AuthenticatedPort587 || direct.State != MailRelayPreflightCheckState.Pass)
                    check = Failed("Enter an SMTP provider hostname, username and password to send securely on TCP/587. LMS cannot discover provider credentials automatically; direct delivery cannot satisfy this selection.");
                else summary = "Automatic: direct TCP/25 selected because no upstream provider is configured. TLS is negotiated where supported.";
            }
            else
            {
                var password = string.IsNullOrWhiteSpace(settings.PasswordSecretReference) ? null : await secrets.ResolveSecretAsync(settings.PasswordSecretReference, cancellationToken);
                if (password?.Any(c => c is '\r' or '\n' or '\0') == true) check = Failed("The provider password contains unsupported control characters. Re-enter it.");
                else if (string.IsNullOrEmpty(password)) check = Failed("Enter the upstream SMTP provider password or app password. Existing LMS SMTP-user credentials are separate.");
                else
                {
                    var status = await probe.CheckAsync(settings.Host, settings.Username, password, cancellationToken);
                    if (status == SmtpSubmissionProbeStatus.Verified)
                    {
                        useUpstream = true;
                        summary = $"Authenticated TCP/587 via {settings.Host}; STARTTLS and the server certificate are verified. Outbound TCP/25 is not required.";
                        check = new(direct.Key, "Outbound delivery", MailRelayPreflightCheckState.Pass, "587 · TLS + AUTH", summary);
                    }
                    else if (status == SmtpSubmissionProbeStatus.Unreachable && settings.Mode == MailRelayOutboundMode.Automatic && direct.State == MailRelayPreflightCheckState.Pass)
                        summary = "Automatic: upstream TCP/587 could not be reached; direct TCP/25 selected. Review this route before installing. TLS is opportunistic on direct delivery.";
                    else check = Failed(status == SmtpSubmissionProbeStatus.Unreachable
                        ? "The upstream SMTP provider could not be reached on TCP/587. Check its hostname, firewall and service availability."
                        : "The provider did not pass STARTTLS, certificate or SMTP authentication checks. Check its credentials and supported authentication. LMS will not bypass this failure by sending directly.");
                }
            }
        }
        if (check.State != MailRelayPreflightCheckState.Pass) summary = check.Detail;
        else if (!useUpstream && destinationPort == 25) check = direct with { Label = "Outbound delivery", Detail = summary, Value = "25 · DIRECT" };
        return new(preflight with { Checks = preflight.Checks.Select(item => item.Key == direct.Key ? check : item).ToArray() }, useUpstream, summary)
            { DestinationPort = useUpstream ? 587 : destinationPort };
    }

    internal static string? Validate(MailRelayOutboundSettings settings)
    {
        if (settings.UsesHttpsApi) return HttpsOutboundMailProvider.Validate(settings);
        if (!Enum.IsDefined(settings.Mode)) return "Choose a supported outbound delivery mode.";
        if (settings.Mode is MailRelayOutboundMode.DirectPort25 or MailRelayOutboundMode.DirectPort587 or MailRelayOutboundMode.AutomaticDirect) return null;
        if (settings.Host.Length > 253 || settings.Host.Length > 0 && (Uri.CheckHostName(settings.Host) != UriHostNameType.Dns || settings.Host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))))
            return "Enter only the SMTP provider DNS hostname, without a scheme, path or port. Submission uses TCP/587.";
        if (settings.Host.Length > 0 && (string.IsNullOrWhiteSpace(settings.Username) || settings.Username.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == ':')))
            return "Enter the provider SMTP username without whitespace, control characters or colons.";
        if (settings.SpfInclude.Length > 0 && (settings.SpfInclude.Length > 253 || settings.SpfInclude.Split('.').Any(label => label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))))
            return "Enter only the SPF include hostname supplied by the provider.";
        return null;
    }
    private static MailRelayPreflightCheck Failed(string detail) => new(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound delivery", MailRelayPreflightCheckState.Failed, "SETUP REQUIRED", detail);
}

// Tests use loopback fixtures; production always validates the provider certificate.
public sealed class SmtpSubmissionProbe : ISmtpSubmissionProbe
{
    private readonly int port;
    private readonly X509Certificate2? fixtureTrustRoot;
    public SmtpSubmissionProbe() : this(587) { }
    internal SmtpSubmissionProbe(int port, X509Certificate2? fixtureTrustRoot = null) { this.port = port; this.fixtureTrustRoot = fixtureTrustRoot; }
    public async Task<SmtpSubmissionProbeStatus> CheckAsync(string host, string username, string password, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var connected = false;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, token);
            connected = true;
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
            if ((await Reply(reader, token)).Code != 220) return SmtpSubmissionProbeStatus.Rejected;
            var ehlo = await Command(stream, reader, "EHLO lms.local", token);
            if (ehlo.Code != 250 || !ehlo.Lines.Any(l => l.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))) return SmtpSubmissionProbeStatus.Rejected;
            if ((await Command(stream, reader, "STARTTLS", token)).Code != 220) return SmtpSubmissionProbeStatus.Rejected;
            await using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            var tlsOptions = new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
            if (fixtureTrustRoot is not null)
            {
                tlsOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
                tlsOptions.CertificateChainPolicy.CustomTrustStore.Add(fixtureTrustRoot);
            }
            await tls.AuthenticateAsClientAsync(tlsOptions, token);
            using var secureReader = new StreamReader(tls, Encoding.ASCII, false, leaveOpen: true);
            ehlo = await Command(tls, secureReader, "EHLO lms.local", token);
            if (ehlo.Code != 250) return SmtpSubmissionProbeStatus.Rejected;
            var methods = ehlo.Lines.Where(l => l.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase)).SelectMany(l => l[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            (int Code, string[] Lines) auth;
            if (methods.Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + username + "\0" + password));
                auth = await Command(tls, secureReader, "AUTH PLAIN " + encoded, token);
                if (auth.Code == 334) auth = await Command(tls, secureReader, encoded, token);
            }
            else if (methods.Contains("LOGIN", StringComparer.OrdinalIgnoreCase))
            {
                auth = await Command(tls, secureReader, "AUTH LOGIN", token);
                if (auth.Code != 334) return SmtpSubmissionProbeStatus.Rejected;
                auth = await Command(tls, secureReader, Convert.ToBase64String(Encoding.UTF8.GetBytes(username)), token);
                if (auth.Code != 334) return SmtpSubmissionProbeStatus.Rejected;
                auth = await Command(tls, secureReader, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), token);
            }
            else return SmtpSubmissionProbeStatus.Rejected;
            if (auth.Code != 235) return SmtpSubmissionProbeStatus.Rejected;
            await tls.WriteAsync(Encoding.ASCII.GetBytes("QUIT\r\n"), token);
            return SmtpSubmissionProbeStatus.Verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is SocketException or IOException or AuthenticationException or OperationCanceledException or InvalidDataException)
        { return connected ? SmtpSubmissionProbeStatus.Rejected : SmtpSubmissionProbeStatus.Unreachable; }
    }
    private static async Task<(int Code, string[] Lines)> Command(Stream stream, StreamReader reader, string command, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), token);
        return await Reply(reader, token);
    }
    private static async Task<(int Code, string[] Lines)> Reply(StreamReader reader, CancellationToken token)
    {
        var lines = new List<string>();
        int? code = null;
        for (var count = 0; count < 64; count++)
        {
            var line = new StringBuilder();
            var buffer = new char[1];
            while (true)
            {
                if (await reader.ReadAsync(buffer.AsMemory(), token) == 0) throw new IOException();
                if (buffer[0] == '\n') break;
                if (line.Length >= 4096) throw new InvalidDataException();
                line.Append(buffer[0]);
            }
            var text = line.ToString().TrimEnd('\r');
            if (text.Length < 4 || !int.TryParse(text[..3], out var current) || code is not null && code != current || text[3] is not ('-' or ' ')) throw new InvalidDataException();
            code = current;
            lines.Add(text[4..]);
            if (text[3] == ' ') return (current, lines.ToArray());
        }
        throw new InvalidDataException();
    }
}
