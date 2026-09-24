// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Services.EdgeGateway;
using Microsoft.AspNetCore.Components;

namespace LinuxMadeSane.Web.Components.Pages;

public partial class EdgeGateway
{
    private const string CaddyTabValue = "caddy";
    private readonly CancellationTokenSource quickSetupCancellation = new();
    private readonly Dictionary<string, ZoneSetupMessage> zoneSetupMessages = new(StringComparer.OrdinalIgnoreCase);
    private MailRelayProvisioningJobSnapshot? quickMailJob;
    private string? busySetupZone;

    [SupplyParameterFromQuery(Name = "tab")]
    public string? Tab { get; set; }

    protected override void OnParametersSet()
    {
        if (Tab is OnDemandAppsTabValue or PublishedAppsTabValue or SetupTabValue or DiagnosticsTabValue or CaddyTabValue)
            activeTab = Tab;
    }

    private bool CanRunZoneSetup(EdgeGatewayCloudflareDomainOption domain) =>
        busySetupZone is null && !IsRouteActionBusy && !isProvisioningCloudflareDomain && !isDeletingRelay &&
        domain is { Paused: false, RelayConfigured: true, RelayUsesCloudflareTunnel: true, RelayOwnedByThisLms: true };

    private static string BuildServerUrl(string domainName)
    {
        try { return $"https://{EdgeGatewayServerPublishingService.BuildServerHostname(Environment.MachineName, domainName)}"; }
        catch (InvalidOperationException) { return $"[hostname].{domainName}"; }
    }

    private async Task PublishServerAsync(EdgeGatewayCloudflareDomainOption domain)
    {
        if (!CanRunZoneSetup(domain)) return;
        busySetupZone = domain.DomainName;
        zoneSetupMessages[domain.DomainName] = new("Checking LMS accounts, server availability, DNS and tunnel hostnames…");
        try
        {
            var result = await ServerPublishing.PublishAsync(domain.DomainName);
            zoneSetupMessages[domain.DomainName] = new(
                result.Success ? $"Published https://{result.Hostname} with MFA security enabled." : result.Summary,
                !result.Success,
                result.Success ? $"https://{result.Hostname}" : null);
            if (!isDisposed) ApplyDashboardSnapshot(
                await LoadDashboardSnapshotAsync(CancellationToken.None),
                isLiveSnapshot: true);
        }
        catch (Exception exception)
        {
            zoneSetupMessages[domain.DomainName] = new(exception.Message, true, HelpUrl: "/settings");
        }
        finally { busySetupZone = null; }
    }

    private async Task EnableServerEmailAsync(EdgeGatewayCloudflareDomainOption domain)
    {
        if (!CanRunZoneSetup(domain) || quickMailJob is { IsTerminal: false }) return;
        busySetupZone = domain.DomainName;
        zoneSetupMessages[domain.DomainName] = new("Checking Mail Relay prerequisites and domain email records…");
        try
        {
            var preview = await ServerEmailSetup.PreviewAsync(domain.DomainName);
            if (!preview.CanInstall)
            {
                zoneSetupMessages[domain.DomainName] = new(
                    string.Join(" ", preview.Errors.DefaultIfEmpty("Mail Relay preflight did not pass. Review the checks in Mail Relay.")),
                    true, HelpUrl: "/mail-relay");
                return;
            }
            quickMailJob = await MailRelayService.StartProvisioningAsync(preview.Request);
            zoneSetupMessages[domain.DomainName] = new(string.Join(" ", preview.Warnings));
        }
        catch (Exception exception)
        {
            zoneSetupMessages[domain.DomainName] = new(exception.Message, true, HelpUrl: "/mail-relay");
        }
        finally { busySetupZone = null; }
    }

    private async Task WatchMailSetupAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                var job = MailRelayService.GetLatestProvisioningJob();
                if (job != quickMailJob)
                {
                    await InvokeAsync(() =>
                    {
                        if (isDisposed) return;
                        quickMailJob = job;
                        StateHasChanged();
                    });
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private sealed record ZoneSetupMessage(string Text, bool IsError = false, string? PublishedUrl = null, string? HelpUrl = null);
}
