// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Security.Cryptography;
using System.Net.Mail;
using System.Text;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayTestServiceTests
{
    [Fact]
    public async Task SendAsync_UsesTheSelectedClientPolicyWithoutRequestingItsPassword()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            RelayHostname = "smtp.example.test"
        };
        var client = new MailRelayClient(
            Guid.NewGuid(), configuration.Id, "Office Printer", "office-printer", "one-way-hash", true,
            ["example.test"], [], 100, 5_000, string.Empty, now, now, null);
        var domain = new MailRelayDomain(
            Guid.NewGuid(), configuration.Id, "zone", "example.test", true, "lms2026", "secret://dkim", now, now,
            null, null, null, null, null, "dkim", "spf", "dmarc",
            MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass,
            MailRelayDmarcPolicy.Monitor, null, now, now);
        var store = new StubStore(configuration, client, domain);
        var runner = new InternalSubmissionRunner();
        var service = new MailRelayTestService(store, runner);

        var result = await service.SendAsync(new MailRelayTestRequest(
            client.Id,
            "printer@example.test",
            "person@example.net"));

        Assert.Equal(MailRelayTestStatus.Sent, result.Status);
        Assert.True(result.ClientPolicyValidated);
        Assert.True(result.AcceptedByRelay);
        Assert.Equal("A1B2C3D4", result.QueueId);
        Assert.NotNull(store.SavedClient?.LastUsedUtc);
        var submission = Assert.Single(runner.Requests, item => item.Description == "Submit an LMS Mail Relay diagnostic message");
        Assert.Contains("/usr/sbin/sendmail", submission.Arguments);
        Assert.NotNull(submission.StandardInputBytes);
        Assert.DoesNotContain("one-way-hash", Encoding.UTF8.GetString(submission.StandardInputBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseLogEvidence_ReportsDestinationAcceptance()
    {
        const string log = "Sep  4 postfix/smtp[23]: A1B2C3: to=<person@example.net>, relay=mx.example.net[192.0.2.1]:25, delay=1.2, dsn=2.0.0, status=sent (250 accepted)";

        var result = MailRelayTestService.ParseLogEvidence(log);

        Assert.NotNull(result);
        Assert.Equal(MailRelayTestStatus.Sent, result.Status);
        Assert.Equal("mx.example.net[192.0.2.1]:25", result.DestinationServer);
        Assert.Equal("250 accepted", result.Detail);
    }

    [Fact]
    public void ParseQueueEvidence_ReturnsTheActualDeferredReason()
    {
        const string queue = "{\"queue_name\":\"deferred\",\"queue_id\":\"A1B2C3\",\"recipients\":[{\"address\":\"person@example.net\",\"delay_reason\":\"connect to mx.example.net: Connection timed out\"}]}";

        var result = MailRelayTestService.ParseQueueEvidence("A1B2C3", queue);

        Assert.NotNull(result);
        Assert.Equal(MailRelayTestStatus.Deferred, result.Status);
        Assert.Contains("Connection timed out", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPassword_ValidatesTheStoredOneWayHash()
    {
        const string password = "Correct-Horse-42";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 12_000, HashAlgorithmName.SHA256, 32);
        var encoded = $"pbkdf2-sha256$12000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

        Assert.True(MailRelayTestService.VerifyPassword(password, encoded));
        Assert.False(MailRelayTestService.VerifyPassword("wrong", encoded));
    }

    [Fact]
    public void ParseQueueIdForMessage_FindsTheInternallySubmittedDiagnostic()
    {
        const string messageId = "lms-test-1234@smtp.example.test";
        const string log = "Sep  4 postfix/cleanup[42]: A1B2C3D4: message-id=<lms-test-1234@smtp.example.test>";

        var queueId = MailRelayTestService.ParseQueueIdForMessage(log, messageId);

        Assert.Equal("A1B2C3D4", queueId);
    }

    [Fact]
    public void BuildInternalDiagnosticMessage_UsesTheSelectedClientAndValidMailHeaders()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            RelayHostname = "smtp.example.test"
        };
        var client = new MailRelayClient(
            Guid.NewGuid(), configuration.Id, "Office Printer", "office-printer", "hash", true,
            ["example.test"], [], 100, 5_000, string.Empty, now, now, null);

        var message = MailRelayTestService.BuildInternalDiagnosticMessage(
            configuration,
            client,
            new MailAddress("printer@example.test"),
            new MailAddress("person@example.net"),
            "lms-test-1234@smtp.example.test");

        Assert.Contains("Message-ID: <lms-test-1234@smtp.example.test>", message, StringComparison.Ordinal);
        Assert.Contains("From: printer@example.test", message, StringComparison.Ordinal);
        Assert.Contains("To: person@example.net", message, StringComparison.Ordinal);
        Assert.Contains("Content-Transfer-Encoding: base64", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubStore(
        MailRelayConfiguration configuration,
        MailRelayClient client,
        MailRelayDomain domain) : IMailRelayStore
    {
        public MailRelayClient? SavedClient { get; private set; }

        public Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MailRelayConfiguration?>(configuration);

        public Task SaveConfigurationAsync(MailRelayConfiguration value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayDomain>>([domain]);

        public Task SaveDomainAsync(MailRelayDomain value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayClient>>([client]);

        public Task SaveClientAsync(MailRelayClient value, CancellationToken cancellationToken = default)
        {
            SavedClient = value;
            return Task.CompletedTask;
        }

        public Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(Guid domainId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class InternalSubmissionRunner : ILinuxCommandRunner
    {
        public List<LinuxCommandRequest> Requests { get; } = [];
        private string messageId = string.Empty;

        public Task<LinuxCommandResult> RunAsync(
            LinuxCommandRequest request,
            bool dryRun,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var output = string.Empty;
            if (request.Description == "Submit an LMS Mail Relay diagnostic message")
            {
                var message = Encoding.UTF8.GetString(request.StandardInputBytes ?? []);
                messageId = message
                    .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                    .Single(line => line.StartsWith("Message-ID: <", StringComparison.Ordinal))
                    [13..^1];
            }
            else if (request.Description == "Find the Mail Relay diagnostic queue ID")
            {
                output = $"Sep  4 postfix/cleanup[42]: A1B2C3D4: message-id=<{messageId}>";
            }
            else if (request.Description == "Read the Mail Relay test delivery result")
            {
                output = "Sep  4 postfix/smtp[43]: A1B2C3D4: to=<person@example.net>, relay=mx.example.net[192.0.2.1]:25, delay=1.2, dsn=2.0.0, status=sent (250 accepted)";
            }

            var timestamp = DateTimeOffset.UtcNow;
            return Task.FromResult(new LinuxCommandResult(request.Description, 0, output, string.Empty, timestamp, timestamp, dryRun));
        }
    }
}
