// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
    IMailRelayPublicIpMonitorService publicIpMonitorService,
    IMailRelayOutboundService? outboundService = null) : IMailRelayService
{
    public async Task<MailRelayDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        // These stores share one scoped DbContext; overlapping reads fail on a cold/slow connection.
        var configuration = await store.GetConfigurationAsync(cancellationToken);
        var domains = await store.ListDomainsAsync(cancellationToken);
        var checkedDomains = new List<MailRelayDomain>(domains.Count);
        foreach (var domain in domains)
            checkedDomains.Add(await provisioningService.CheckDomainDnsAsync(domain, cancellationToken));
        var clients = await store.ListClientsAsync(cancellationToken);
        var preflight = await preflightService.InspectAsync(false, cancellationToken: cancellationToken);
        preflight = await ApplyConfiguredRouteAsync(preflight, configuration, cancellationToken);
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
            checkedDomains,
            clients,
            preflight) { Listeners = configuration?.Enabled == true ? await provisioningService.ReadListenersAsync(cancellationToken) : null };
    }

    public async Task<MailRelayPreflightResult> InspectPreflightAsync(string? cloudflareZoneId, CancellationToken cancellationToken = default) =>
        await ApplyConfiguredRouteAsync(await preflightService.InspectAsync(false, cloudflareZoneId, cancellationToken), await store.GetConfigurationAsync(cancellationToken), cancellationToken);

    public async Task<MailRelayPreflightResult> RunPreflightAsync(string? cloudflareZoneId = null, CancellationToken cancellationToken = default) =>
        await ApplyConfiguredRouteAsync(await preflightService.InspectAsync(true, cloudflareZoneId, cancellationToken), await store.GetConfigurationAsync(cancellationToken), cancellationToken);

    private async Task<MailRelayPreflightResult> ApplyConfiguredRouteAsync(MailRelayPreflightResult preflight, MailRelayConfiguration? configuration, CancellationToken token)
    {
        if (outboundService is null || configuration is null) return preflight;
        // Check the route actually installed; an outage must not claim a new route is active.
        var settings = configuration.Outbound with { Mode = configuration.Outbound.UsesHttpsApi ? configuration.Outbound.Mode : configuration.DeliveryMode == MailRelayDeliveryMode.UpstreamSmtp
            ? MailRelayOutboundMode.AuthenticatedPort587 : configuration.Outbound.DestinationPort == 587 ? MailRelayOutboundMode.DirectPort587 : MailRelayOutboundMode.DirectPort25 };
        return (await outboundService.InspectAsync(settings, preflight, token)).Preflight;
    }

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
