// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayPublicIpMonitorServiceTests
{
    [Fact]
    public async Task SaveSettings_EnablesCustomScheduleAndPersistsState()
    {
        var store = new StubMailRelayStore(MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with
        {
            Enabled = true,
            PublicIpAddress = "203.0.113.10"
        });
        var services = new ServiceCollection();
        services.AddSingleton<IMailRelayStore>(store);
        await using var provider = services.BuildServiceProvider();
        var service = new MailRelayPublicIpMonitorService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailRelayPublicIpMonitorService>.Instance);

        var saved = await service.SaveSettingsAsync(new MailRelayPublicIpMonitorSettingsRequest(true, 90));

        Assert.True(saved.MonitorPublicIpChanges);
        Assert.Equal(90, saved.PublicIpCheckIntervalMinutes);
        Assert.Equal(MailRelayPublicIpMonitorStatus.NotChecked, saved.PublicIpMonitorStatus);
        Assert.Equal(saved, store.Configuration);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(1441)]
    public async Task SaveSettings_RejectsIntervalsOutsideSafeRange(int minutes)
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var service = new MailRelayPublicIpMonitorService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailRelayPublicIpMonitorService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveSettingsAsync(new MailRelayPublicIpMonitorSettingsRequest(true, minutes)));
    }

    private sealed class StubMailRelayStore(MailRelayConfiguration configuration) : IMailRelayStore
    {
        public MailRelayConfiguration Configuration { get; private set; } = configuration;

        public Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<MailRelayConfiguration?>(Configuration);

        public Task SaveConfigurationAsync(MailRelayConfiguration value, CancellationToken cancellationToken = default)
        {
            Configuration = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDomainAsync(MailRelayDomain domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveClientAsync(MailRelayClient client, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(Guid domainId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
