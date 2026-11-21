// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayProvisioningQueueTests
{
    [Fact]
    public async Task OnlyCoalescesRequestsForTheSameSetup()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var queue = new MailRelayProvisioningQueue(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailRelayProvisioningQueue>.Instance);
        var queued = await queue.EnqueueAsync(Request());
        Assert.Equal(queued.Id, (await queue.EnqueueAsync(Request())).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(Request() with { SendingDomain = "other.test" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(Request() with { ConfigureLmsEmail = true }));
        Assert.Equal(queued, queue.GetLatestJob());
    }

    [Fact]
    public async Task BackgroundJob_RetainsProgressAndResultForAReconnectedPage()
    {
        var services = new ServiceCollection();
        services.AddScoped<IMailRelayProvisioningService, SuccessfulProvisioner>();
        await using var provider = services.BuildServiceProvider();
        var queue = new MailRelayProvisioningQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailRelayProvisioningQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        try
        {
            var queued = await queue.EnqueueAsync(Request());
            var completed = await WaitForTerminalAsync(queue, queued.Id);

            Assert.Equal(MailRelayProvisioningJobStatus.Succeeded, completed.Status);
            Assert.True(completed.IsTerminal);
            Assert.Equal(queued.Id, queue.GetLatestJob()?.Id);
            Assert.Equal("example.test", queue.GetJob(queued.Id)?.Request.SendingDomain);
            Assert.Contains(completed.Steps, item => item.Key == "docker" && item.State == MailRelaySetupStepState.Complete);
            Assert.Equal("generated-once", completed.Result?.GeneratedPassword);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
            queue.Dispose();
        }
    }

    private static async Task<MailRelayProvisioningJobSnapshot> WaitForTerminalAsync(
        MailRelayProvisioningQueue queue,
        Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = queue.GetJob(jobId);
            if (snapshot?.IsTerminal == true)
            {
                return snapshot;
            }

            await Task.Delay(20, timeout.Token);
        }

        throw new TimeoutException("Mail Relay job did not finish.");
    }

    private static MailRelaySetupRequest Request() =>
        new("zone", "smtp.example.test", "example.test", "lms2026", "Flow", "flow", true, false);

    private sealed class SuccessfulProvisioner : IMailRelayProvisioningService
    {
        public Task<IReadOnlyList<MailRelayListener>?> ReadListenersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MailRelayListener>?>([]);
        public Task<LinuxMadeSane.Core.Models.MailRelay.MailRelayDomain> CheckDomainDnsAsync(LinuxMadeSane.Core.Models.MailRelay.MailRelayDomain domain, CancellationToken cancellationToken) => Task.FromResult(domain);

        public Task<MailRelayPublicIpSyncResult> SynchronizePublicIpAsync(string detectedPublicIp, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MailRelaySetupPreview> PreviewAsync(MailRelaySetupRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MailRelaySetupResult> ProvisionAsync(
            MailRelaySetupRequest request,
            IProgress<MailRelaySetupProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var step = new MailRelaySetupProgressUpdate(
                "docker",
                "Prepare Docker",
                MailRelaySetupStepState.Complete,
                "Docker is running.");
            progress?.Report(step);
            return Task.FromResult(new MailRelaySetupResult(
                true,
                request.RelayHostname,
                "localhost",
                587,
                request.ApplicationUsername,
                "generated-once",
                request.SendingDomain,
                request.DkimSelector,
                true,
                [step],
                "Mail Relay is running."));
        }

        public Task<MailRelayLegacySubmissionResult> ConfigureLegacySubmissionAsync(
            MailRelayLegacySubmissionRequest request,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MailRelayRemovalResult> RemoveAsync(
            MailRelayRemovalRequest request,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
