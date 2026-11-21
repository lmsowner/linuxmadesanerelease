// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.Messaging;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class ManagedMailRelaySmtpClientTests
{
    [Fact]
    public async Task SendAsync_PinsTheManagedSelfSignedCertificateAndSubmitsTheMessage()
    {
        using var certificate = CreateCertificate();
        var certificatePem = certificate.ExportCertificatePem();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = RunServerAsync(listener, certificate, timeout.Token);
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            RelayHostname = "smtp.example.test",
            SubmissionPort = port
        };

        var result = await ManagedMailRelaySmtpClient.SendAsync(
            configuration,
            "lms-notifications",
            "correct horse battery staple",
            new MailAddress("notifications@example.test"),
            new MailAddress("person@example.net"),
            "LMS messaging test",
            "<p>Working</p>",
            "text/html",
            certificatePem,
            timeout.Token);
        var receivedMessage = await server;

        Assert.True(result.Authenticated);
        Assert.True(result.Accepted);
        Assert.Equal("A1B2C3", result.QueueId);
        Assert.Contains("Content-Type: text/html; charset=utf-8", receivedMessage, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>Working</p>")), receivedMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredEmailDelivery_UsesTheManagedRelayAndOnlyOtpHasAnExpiry(bool isOtp)
    {
        using var certificate = CreateCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = RunServerAsync(listener, certificate, timeout.Token);
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            RelayHostname = "smtp.example.test",
            SubmissionPort = port,
            TlsCertificateSecretReference = "secret://relay-certificate"
        };
        var relayClient = new MailRelayClient(
            Guid.NewGuid(), configuration.Id, "LMS Notifications", "lms-notifications", "hash", true,
            ["example.test"], [], 100, 5_000, string.Empty, now, now, null);
        var settings = MessagingEmailSettings.CreateDefault(now) with
        {
            IsEnabled = true,
            Provider = MessagingEmailProvider.Smtp,
            SenderAddress = "notifications@example.test",
            SmtpHost = "100.100.100.100",
            SmtpPort = 587,
            SmtpUseStartTls = true,
            SmtpUsername = relayClient.Username,
            SmtpPasswordSecretReference = "secret://smtp-password"
        };
        var mailRelayStore = new StubMailRelayStore(configuration, relayClient);
        var service = new ConfiguredEmailDeliveryService(
            new StubMessagingStore(settings),
            mailRelayStore,
            new StubSecretStore(
                ("secret://relay-certificate", certificate.ExportCertificatePem()),
                ("secret://smtp-password", "correct horse battery staple")),
            new StubHttpClientFactory());

        var before = DateTimeOffset.UtcNow;
        var result = isOtp
            ? await service.SendOtpHtmlAsync("person@example.net", "LMS sign-in code", "<p>123456</p>", timeout.Token)
            : await service.SendHtmlAsync("person@example.net", "LMS messaging test", "<p>Working</p>", timeout.Token);
        var message = await server;
        var expiryHeaders = message.Replace("\r\n", "\n").Split('\n').Where(line => line.StartsWith("Expires: ")).ToArray();
        if (isOtp)
        {
            var expiry = DateTimeOffset.Parse(Assert.Single(expiryHeaders)[9..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(expiry, before.AddHours(24).AddSeconds(-1), DateTimeOffset.UtcNow.AddHours(24));
        }
        else Assert.Empty(expiryHeaders);

        Assert.True(result.Succeeded);
        Assert.Contains("accepted by LMS Mail Relay", result.Message, StringComparison.Ordinal);
        Assert.NotNull(mailRelayStore.SavedClient?.LastUsedUtc);
    }

    private static async Task<string> RunServerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var network = client.GetStream();
        await WriteAsync(network, "220 smtp.example.test ESMTP\r\n", cancellationToken);
        using (var reader = new StreamReader(network, Encoding.ASCII, false, leaveOpen: true))
        {
            Assert.StartsWith("EHLO ", await reader.ReadLineAsync(cancellationToken), StringComparison.Ordinal);
            await WriteAsync(network, "250-smtp.example.test\r\n250 STARTTLS\r\n", cancellationToken);
            Assert.Equal("STARTTLS", await reader.ReadLineAsync(cancellationToken));
            await WriteAsync(network, "220 Ready to start TLS\r\n", cancellationToken);
        }

        await using var tls = new SslStream(network, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            },
            cancellationToken);
        using var tlsReader = new StreamReader(tls, Encoding.ASCII, false, leaveOpen: true);
        Assert.StartsWith("EHLO ", await tlsReader.ReadLineAsync(cancellationToken), StringComparison.Ordinal);
        await WriteAsync(tls, "250-smtp.example.test\r\n250 AUTH PLAIN\r\n", cancellationToken);
        Assert.StartsWith("AUTH PLAIN ", await tlsReader.ReadLineAsync(cancellationToken), StringComparison.Ordinal);
        await WriteAsync(tls, "235 2.7.0 Authentication successful\r\n", cancellationToken);
        Assert.StartsWith("MAIL FROM:", await tlsReader.ReadLineAsync(cancellationToken), StringComparison.Ordinal);
        await WriteAsync(tls, "250 2.1.0 Ok\r\n", cancellationToken);
        Assert.StartsWith("RCPT TO:", await tlsReader.ReadLineAsync(cancellationToken), StringComparison.Ordinal);
        await WriteAsync(tls, "250 2.1.5 Ok\r\n", cancellationToken);
        Assert.Equal("DATA", await tlsReader.ReadLineAsync(cancellationToken));
        await WriteAsync(tls, "354 End data with <CR><LF>.<CR><LF>\r\n", cancellationToken);

        var message = new StringBuilder();
        while (true)
        {
            var line = await tlsReader.ReadLineAsync(cancellationToken);
            if (line == ".")
            {
                break;
            }

            Assert.NotNull(line);
            message.AppendLine(line);
        }

        await WriteAsync(tls, "250 2.0.0 Ok: queued as A1B2C3\r\n", cancellationToken);
        Assert.Equal("QUIT", await tlsReader.ReadLineAsync(cancellationToken));
        await WriteAsync(tls, "221 2.0.0 Bye\r\n", cancellationToken);
        return message.ToString();
    }

    private static async Task WriteAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=smtp.example.test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("smtp.example.test");
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class StubMessagingStore(MessagingEmailSettings settings) : IMessagingEmailSettingsStore
    {
        public Task<MessagingEmailSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task SaveAsync(MessagingEmailSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubMailRelayStore(
        MailRelayConfiguration configuration,
        MailRelayClient client) : IMailRelayStore
    {
        public MailRelayClient? SavedClient { get; private set; }

        public Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MailRelayConfiguration?>(configuration);

        public Task SaveConfigurationAsync(MailRelayConfiguration value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayDomain>>([]);

        public Task SaveDomainAsync(MailRelayDomain domain, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayClient>>([client]);

        public Task SaveClientAsync(MailRelayClient value, CancellationToken cancellationToken = default)
        {
            SavedClient = value;
            return Task.CompletedTask;
        }

        public Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(Guid domainId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayDnsRecord>>([]);

        public Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubSecretStore(params (string Reference, string Value)[] values) : ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string> secrets = values.ToDictionary(item => item.Reference, item => item.Value);

        public Task<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(secrets.TryGetValue(secretReference, out var value) ? value : null);

        public Task<SecretReferenceMetadata?> GetMetadataAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult<SecretReferenceMetadata?>(null);

        public Task<string> StoreSecretAsync(string secretValue, string purpose, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
