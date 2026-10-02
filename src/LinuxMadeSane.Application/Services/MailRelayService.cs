// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Application.Services;

public sealed class MailRelayService(
    IMailRelayStore store,
    IMailRelayPreflightService preflightService,
    IMailRelayProvisioningService provisioningService,
    IMailRelayProvisioningQueue provisioningQueue,
    IMailRelayTestService testService,
    IMailRelayClientService clientService,
    IMailRelayPublicIpMonitorService publicIpMonitorService) : IMailRelayService
{
    public async Task<MailRelayDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        // These stores share one scoped DbContext; overlapping reads fail on a cold/slow connection.
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        var domains = await store.ListDomainsAsync(cancellationToken);
        var clients = await store.ListClientsAsync(cancellationToken);
        var preflight = await preflightService.InspectAsync(false, cancellationToken: cancellationToken);
        var status = configuration is null
            ? MailRelayOperationalStatus.NotConfigured
            : configuration.Enabled
                ? MailRelayOperationalStatus.Warning
                : MailRelayOperationalStatus.NotConfigured;

        var summary = configuration is null
            ? preflight.CanConfigure
                ? "This host is suitable. Mail Relay has not been configured yet."
                : "Complete the required preflight checks before configuring Mail Relay."
            : configuration.Enabled
                ? $"Mail Relay is configured at {configuration.RelayHostname}:{configuration.SubmissionPort}."
                : "Mail Relay configuration is saved but disabled.";

        return new MailRelayDashboardViewModel(
            status,
            summary,
            configuration,
            domains,
            clients,
            preflight);
    }

    public Task<MailRelayPreflightResult> InspectPreflightAsync(
        string? cloudflareZoneId,
        CancellationToken cancellationToken = default) =>
        preflightService.InspectAsync(false, cloudflareZoneId, cancellationToken);

    public Task<MailRelayPreflightResult> RunPreflightAsync(
        string? cloudflareZoneId = null,
        CancellationToken cancellationToken = default) =>
        preflightService.InspectAsync(true, cloudflareZoneId, cancellationToken);

    public Task<MailRelaySetupPreview> PreviewSetupAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default) =>
        provisioningService.PreviewAsync(request, cancellationToken);

    public Task<MailRelayProvisioningJobSnapshot> StartProvisioningAsync(
        MailRelaySetupRequest request,
        CancellationToken cancellationToken = default) =>
        provisioningQueue.EnqueueAsync(request, cancellationToken);

    public Task<MailRelayTestResult> SendTestAsync(
        MailRelayTestRequest request,
        CancellationToken cancellationToken = default) =>
        testService.SendAsync(request, cancellationToken);

    public string GenerateClientPassword() => clientService.GeneratePassword();

    public Task<MailRelayConfiguration> SavePublicIpMonitorSettingsAsync(
        MailRelayPublicIpMonitorSettingsRequest request,
        CancellationToken cancellationToken = default) =>
        publicIpMonitorService.SaveSettingsAsync(request, cancellationToken);

    public Task<MailRelayPublicIpSyncResult> CheckPublicIpNowAsync(
        CancellationToken cancellationToken = default) =>
        publicIpMonitorService.CheckNowAsync(cancellationToken);

    public Task<MailRelayClientSaveResult> SaveClientAsync(
        MailRelayClientSaveRequest request,
        CancellationToken cancellationToken = default) =>
        clientService.SaveAsync(request, cancellationToken);

    public Task<MailRelayLegacySubmissionResult> ConfigureLegacySubmissionAsync(
        MailRelayLegacySubmissionRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        provisioningService.ConfigureLegacySubmissionAsync(request, progress, cancellationToken);

    public Task<MailRelayRemovalResult> RemoveAsync(
        MailRelayRemovalRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        provisioningService.RemoveAsync(request, progress, cancellationToken);

    public MailRelayProvisioningJobSnapshot? GetProvisioningJob(Guid jobId) =>
        provisioningQueue.GetJob(jobId);

    public MailRelayProvisioningJobSnapshot? GetLatestProvisioningJob() =>
        provisioningQueue.GetLatestJob();
}
