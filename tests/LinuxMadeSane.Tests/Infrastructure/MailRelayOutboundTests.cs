// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Reflection;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Tests.Infrastructure;
public sealed class MailRelayOutboundTests
{
    internal static MailRelayPreflightResult Preflight(bool direct) => new("zone", "example.test", [], "smtp.example.test", "203.0.113.1", "", new[] {
        MailRelayPreflightCheckKeys.EdgeGateway, MailRelayPreflightCheckKeys.CloudflareAuthentication, MailRelayPreflightCheckKeys.CloudflareZone,
        MailRelayPreflightCheckKeys.DnsList, MailRelayPreflightCheckKeys.DnsEdit, MailRelayPreflightCheckKeys.PublicIpv4, MailRelayPreflightCheckKeys.OutboundSmtp,
        MailRelayPreflightCheckKeys.Docker, MailRelayPreflightCheckKeys.ReverseDns, MailRelayPreflightCheckKeys.Tailscale }.Select(key =>
            new MailRelayPreflightCheck(key, key, key == MailRelayPreflightCheckKeys.OutboundSmtp && !direct ? MailRelayPreflightCheckState.Failed : MailRelayPreflightCheckState.Pass, "", "")).ToArray(), true, DateTimeOffset.UtcNow);
    internal static MailRelayOutboundSettings Settings(MailRelayOutboundMode mode = MailRelayOutboundMode.Automatic) => new() { Mode = mode, Host = "smtp.provider.test", Username = "provider-user", PasswordSecretReference = "secret://upstream" };

    [Theory]
    [InlineData(MailRelayOutboundMode.DirectPort25, true, 25, true)]
    [InlineData(MailRelayOutboundMode.DirectPort587, false, 587, true)]
    [InlineData(MailRelayOutboundMode.AutomaticDirect, true, 25, true)]
    [InlineData(MailRelayOutboundMode.AutomaticDirect, false, 587, true)]
    [InlineData(MailRelayOutboundMode.DirectPort25, false, 25, false)]
    public async Task DirectModesSelectDestinationPortWithoutProviderCredentials(MailRelayOutboundMode mode, bool port25Available, int expectedPort, bool canConfigure)
    {
        var probe = new Probe(SmtpSubmissionProbeStatus.Rejected);
        var result = await Service(probe).InspectAsync(new() { Mode = mode }, Preflight(port25Available));
        Assert.Equal(expectedPort, result.DestinationPort);
        Assert.Equal(canConfigure, result.Preflight.CanConfigure);
        Assert.False(result.UseUpstream);
        Assert.Equal(0, probe.Calls);
        if (expectedPort == 587) Assert.Equal(MailRelayPreflightCheckState.Warning, result.Preflight.GetCheck(MailRelayPreflightCheckKeys.OutboundSmtp).State);
    }
    [Fact]
    public void Direct587UsesRecipientDomainMxLookupAndNoConfiguredRelayOrSasl()
    {
        var config = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Outbound = new() { Mode = MailRelayOutboundMode.DirectPort587, DestinationPort = 587 } };
        var text = MailRelayProvisioningService.BuildMainCf(config);
        Assert.Contains("smtp_tcp_port = 587", text);
        Assert.Contains("transport_maps =\n", text);
        Assert.Contains("smtp_sasl_auth_enable = no", text);
        Assert.Contains("smtp_tls_security_level = verify", text);
        Assert.DoesNotContain("smtp_sasl_password_maps", text);
        Assert.Contains("relayhost =\n", text);
        Assert.Contains("smtp_tcp_port = 25", MailRelayProvisioningService.BuildMainCf(config with { Outbound = new() }));
    }

    [Theory]
    [InlineData(MailRelayOutboundMode.AuthenticatedPort587)]
    [InlineData(MailRelayOutboundMode.Automatic)]
    public async Task VerifiedProviderUnblocksAzureWithoutDirectPort25(MailRelayOutboundMode mode)
    {
        var probe = new Probe(SmtpSubmissionProbeStatus.Verified);
        var inspection = await Service(probe).InspectAsync(Settings(mode), Preflight(false));
        Assert.True(inspection.Preflight.CanConfigure);
        Assert.True(inspection.UseUpstream);
        Assert.Contains("TCP/25 is not required", inspection.Summary);
        Assert.Equal(1, probe.Calls);
    }
    [Theory]
    [InlineData(SmtpSubmissionProbeStatus.Rejected, false)]
    [InlineData(SmtpSubmissionProbeStatus.Unreachable, true)]
    public async Task AutoFallbackRequiresReachableDirectPortAndNeverBypassesAuthOrTlsFailure(SmtpSubmissionProbeStatus status, bool canUseDirect)
    {
        var inspection = await Service(new Probe(status)).InspectAsync(Settings(), Preflight(true));
        Assert.Equal(canUseDirect, inspection.Preflight.CanConfigure);
        Assert.False(inspection.UseUpstream);
    }
    [Fact]
    public async Task Required587DoesNotFallbackEvenWhenDirectPortIsAvailable()
    {
        var inspection = await Service(new Probe(SmtpSubmissionProbeStatus.Unreachable)).InspectAsync(Settings(MailRelayOutboundMode.AuthenticatedPort587), Preflight(true));
        Assert.False(inspection.Preflight.CanConfigure);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticWithoutProviderUsesDirectOnlyIfAvailable(bool direct)
    {
        var probe = new Probe(SmtpSubmissionProbeStatus.Verified);
        var inspection = await Service(probe).InspectAsync(new() { Mode = MailRelayOutboundMode.Automatic }, Preflight(direct));
        Assert.Equal(direct, inspection.Preflight.CanConfigure);
        Assert.Equal(0, probe.Calls);
        Assert.False(inspection.UseUpstream);
    }
    [Theory]
    [InlineData("smtp.test\nrelayhost=evil", "user")]
    [InlineData("https://smtp.test", "user")]
    [InlineData("smtp.test:587", "user")]
    [InlineData("smtp.test", "user\nother")]
    [InlineData("smtp.test", "user:password")]
    public async Task InvalidSettingsNeverReachTheNetwork(string host, string username)
    {
        var probe = new Probe(SmtpSubmissionProbeStatus.Verified);
        var result = await Service(probe).InspectAsync(Settings() with { Host = host, Username = username }, Preflight(true));
        Assert.False(result.Preflight.CanConfigure);
        Assert.Equal(0, probe.Calls);
    }
    [Fact]
    public void PostfixUsesVerifiedTlsAndProtectedSaslMapWithoutEmbeddingPasswordInMainConfiguration()
    {
        var config = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { DeliveryMode = MailRelayDeliveryMode.UpstreamSmtp, Outbound = Settings() };
        var text = MailRelayProvisioningService.BuildMainCf(config);
        Assert.Contains("relayhost = [smtp.provider.test]:587", text);
        Assert.Contains("smtp_tls_security_level = verify", text);
        Assert.Contains("smtp_sasl_password_maps = texthash:/etc/postfix/sasl_passwd", text);
        Assert.DoesNotContain("test-password", text);
        Assert.Equal("[smtp.provider.test]:587 provider-user:test-password", MailRelayProvisioningService.BuildUpstreamPasswordMap(config, "test-password"));
        Assert.Throws<InvalidOperationException>(() => MailRelayProvisioningService.BuildUpstreamPasswordMap(config, "secret\nrelayhost=evil"));
        var direct = MailRelayProvisioningService.BuildMainCf(config with { DeliveryMode = MailRelayDeliveryMode.DirectInternet });
        Assert.DoesNotContain("[smtp.provider.test]", direct);
        Assert.Contains("smtp_sasl_auth_enable = no", direct);
    }

    [Theory]
    [InlineData(true, true, true, SmtpSubmissionProbeStatus.Verified)]
    [InlineData(true, true, false, SmtpSubmissionProbeStatus.Rejected)]
    [InlineData(true, false, true, SmtpSubmissionProbeStatus.Rejected)]
    [InlineData(false, false, true, SmtpSubmissionProbeStatus.Rejected)]
    public async Task ActualSmtpHandshakeRequiresTrustedStartTlsBeforeSendingCredentials(bool startTls, bool trusted, bool acceptsAuth, SmtpSubmissionProbeStatus expected)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var authSeen = false;
        var server = Task.Run(async () => {
            using var connection = await listener.AcceptTcpClientAsync(limit.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
            async Task Write(Stream target, string text) => await target.WriteAsync(Encoding.ASCII.GetBytes(text), limit.Token);
            await Write(stream, "220 fixture\r\n");
            Assert.StartsWith("EHLO ", await reader.ReadLineAsync(limit.Token));
            await Write(stream, startTls ? "250-fixture\r\n250 STARTTLS\r\n" : "250 fixture\r\n");
            if (!startTls) return;
            Assert.Equal("STARTTLS", await reader.ReadLineAsync(limit.Token));
            await Write(stream, "220 start TLS\r\n");
            await using var tls = new SslStream(stream, true);
            try {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, limit.Token);
                using var secureReader = new StreamReader(tls, Encoding.ASCII, false, leaveOpen: true);
                var hello = await secureReader.ReadLineAsync(limit.Token);
                if (hello is null) return;
                Assert.StartsWith("EHLO ", hello);
                await Write(tls, "250-fixture\r\n250 AUTH PLAIN\r\n");
                var auth = await secureReader.ReadLineAsync(limit.Token);
                Assert.StartsWith("AUTH PLAIN ", auth);
                authSeen = true;
                Assert.Equal("\0user\0test-password", Encoding.UTF8.GetString(Convert.FromBase64String(auth![11..])));
                await Write(tls, acceptsAuth ? "235 authenticated\r\n" : "535 authentication rejected\r\n");
            } catch (Exception ex) when (!trusted && ex is AuthenticationException or IOException) { }
        }, limit.Token);
        try {
            var probe = new SmtpSubmissionProbe(((IPEndPoint)listener.LocalEndpoint).Port, trusted ? cert : null);
            Assert.Equal(expected, await probe.CheckAsync("localhost", "user", "test-password", limit.Token));
            await server;
            Assert.Equal(startTls && trusted, authSeen);
        } finally { listener.Stop(); }
    }
    internal sealed class Probe(SmtpSubmissionProbeStatus status) : ISmtpSubmissionProbe
    {
        public int Calls;
        public Task<SmtpSubmissionProbeStatus> CheckAsync(string host, string username, string password, CancellationToken token) { Calls++; return Task.FromResult(status); }
    }
    internal static MailRelayOutboundService Service(ISmtpSubmissionProbe probe) => new(DispatchProxy.Create<ISecretStore, Secrets>(), probe);
    public class Secrets : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(ISecretStore.ResolveSecretAsync) ? Task.FromResult<string?>("test-password") : throw new NotSupportedException();
    }
}
