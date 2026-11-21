// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Text;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Services;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayClientServiceTests
{
    [Fact]
    public async Task Save_AddsTheCredentialToPostfixWithoutPuttingThePasswordInArgumentsOrPersistence()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            RelayHostname = "smtp.example.test"
        };
        var domain = Domain(configuration.Id, "example.test", now);
        var store = new StubStore(configuration, [domain]);
        var runner = new RecordingCommandRunner();
        var service = new MailRelayClientService(store, runner);
        const string password = "An-Explicit-Test-Password-42";

        var result = await service.SaveAsync(new MailRelayClientSaveRequest(
            null,
            "Flow Production",
            "flow-prod",
            password,
            [domain.Id]));

        Assert.True(result.Success);
        Assert.True(result.PasswordChanged);
        var saved = Assert.IsType<MailRelayClient>(store.SavedClient);
        Assert.Equal(["example.test"], saved.AllowedSenderDomains);
        Assert.DoesNotContain(password, saved.PasswordHash, StringComparison.Ordinal);
        Assert.True(MailRelayTestService.VerifyPassword(password, saved.PasswordHash));
        Assert.DoesNotContain(runner.Requests.SelectMany(item => item.Arguments), argument => argument.Contains(password, StringComparison.Ordinal));
        var passwordCommand = Assert.Single(runner.Requests, item => item.Description == "Set the Mail Relay SMTP user password");
        Assert.NotNull(passwordCommand.StandardInputBytes);
        Assert.Equal(password + "\n", Encoding.UTF8.GetString(passwordCommand.StandardInputBytes));
        Assert.Contains(runner.Requests, item => item.Description == "Reload Mail Relay sender permissions");
    }

    [Fact]
    public async Task Save_RequiresAnExplicitConfiguredSenderDomain()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with { Enabled = true };
        var service = new MailRelayClientService(new StubStore(configuration, []), new RecordingCommandRunner());

        var result = await service.SaveAsync(new MailRelayClientSaveRequest(
            null,
            "Flow Production",
            "flow-prod",
            "An-Explicit-Test-Password-42",
            []));

        Assert.False(result.Success);
        Assert.Contains("configured sending domain", result.Summary, StringComparison.Ordinal);
    }

    private static MailRelayDomain Domain(Guid configurationId, string name, DateTimeOffset now) =>
        new(
            Guid.NewGuid(), configurationId, "zone", name, true, "lms2026", "secret://dkim", now, now,
            null, null, null, null, null, "dkim", "spf", "dmarc",
            MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass,
            MailRelayDmarcPolicy.Monitor, null, now, now);

    private sealed class RecordingCommandRunner : ILinuxCommandRunner
    {
        public List<LinuxCommandRequest> Requests { get; } = [];

        public Task<LinuxCommandResult> RunAsync(
            LinuxCommandRequest request,
            bool dryRun,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new LinuxCommandResult(request.Description, 0, string.Empty, string.Empty, now, now, dryRun));
        }
    }

    private sealed class StubStore(
        MailRelayConfiguration configuration,
        IReadOnlyList<MailRelayDomain> domains) : IMailRelayStore
    {
        public MailRelayClient? SavedClient { get; private set; }

        public Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MailRelayConfiguration?>(configuration);

        public Task SaveConfigurationAsync(MailRelayConfiguration value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default) => Task.FromResult(domains);
        public Task SaveDomainAsync(MailRelayDomain domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayClient>>(SavedClient is null ? [] : [SavedClient]);

        public Task SaveClientAsync(MailRelayClient client, CancellationToken cancellationToken = default)
        {
            SavedClient = client;
            return Task.CompletedTask;
        }

        public Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(Guid domainId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
