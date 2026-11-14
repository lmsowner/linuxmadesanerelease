// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Services.EdgeGateway;
using Microsoft.AspNetCore.Components;

namespace LinuxMadeSane.Web.Components.Pages;

public partial class EdgeGateway
{
    private EdgeGatewayRouteListItem? mfaRoute;
    private IReadOnlyList<string> mfaEmails = [];
    private string mfaEmail = "", mfaCode = "";
    private string? mfaMessage;
    private bool mfaBusy, mfaSucceeded;
    private IReadOnlyDictionary<string, Guid> mfaUserIds = new Dictionary<string, Guid>();
    private EdgeGatewayMfaEnrollmentService.EnrollmentQr? mfaQr;

    private string? MfaPasskeySetupHref
    {
        get
        {
            if (mfaRoute is null) return null;
            var external = new Uri(BuildExternalRouteUrl(mfaRoute));
            return new UriBuilder(external)
            {
                Path = EdgeGatewayAuthenticationPaths.Login,
                Query = $"method=authenticator&email={Uri.EscapeDataString(mfaEmail)}&returnUrl={Uri.EscapeDataString(external.PathAndQuery)}",
                Fragment = string.Empty
            }.Uri.ToString();
        }
    }

    private async Task OpenMfaSetupAsync(EdgeGatewayRouteListItem route)
    {
        if (mfaBusy) return;
        CloseMfaSetup();
        mfaRoute = route;
        mfaBusy = true;
        mfaMessage = "Loading accounts…";
        try
        {
            var users = await SecurityUserStore.ListAsync();
            mfaUserIds = users.Where(user => user.IsEnabled).ToDictionary(user => user.Email, user => user.Id);
            mfaEmails = mfaUserIds.Keys.OrderBy(email => email, StringComparer.OrdinalIgnoreCase).ToArray();
            mfaMessage = mfaEmails.Count == 0 ? "No enabled LMS accounts are available." : null;
        }
        catch { mfaMessage = "Could not load LMS accounts. Close and try again."; }
        finally { mfaBusy = false; }
    }

    private async Task SelectMfaEmailAsync(string email)
    {
        if (mfaBusy) return;
        MfaEnrollment.Cancel();
        mfaEmail = email; mfaCode = ""; mfaQr = null; mfaSucceeded = false; mfaMessage = null;
        if (mfaRoute is null || !mfaUserIds.TryGetValue(email, out var userId)) return;
        mfaBusy = true;
        mfaMessage = "Preparing authenticator…";
        try { mfaQr = await MfaEnrollment.BeginAsync(mfaRoute.Id, userId); mfaMessage = null; }
        catch (InvalidOperationException exception) { mfaMessage = exception.Message; }
        catch { mfaMessage = "Could not prepare MFA. Select the email and try again."; }
        finally { mfaBusy = false; }
    }

    private async Task ConfirmMfaAsync()
    {
        if (mfaBusy || mfaQr is null) return;
        mfaBusy = true; mfaMessage = "Checking authenticator code…";
        try
        {
            await MfaEnrollment.ConfirmAsync(mfaCode);
            mfaSucceeded = true; mfaQr = null; mfaCode = "";
            mfaMessage = $"Code verified. MFA is ready for {mfaEmail} at {mfaRoute!.Hostname}.";
        }
        catch (InvalidOperationException exception) { mfaMessage = exception.Message; }
        catch { mfaMessage = "MFA could not be saved. Try again."; }
        finally { mfaBusy = false; }
    }

    private void CloseMfaSetup()
    {
        if (mfaBusy) return;
        MfaEnrollment.Cancel(); mfaRoute = null; mfaQr = null;
        mfaEmail = ""; mfaCode = ""; mfaMessage = null; mfaSucceeded = false;
    }

    private const string CaddyTabValue = "caddy";
    private const string MailRelayTabValue = "mail-relay";
    private readonly CancellationTokenSource quickSetupCancellation = new();
    private readonly Dictionary<string, ZoneSetupMessage> zoneSetupMessages = new(StringComparer.OrdinalIgnoreCase);
    private MailRelayProvisioningJobSnapshot? quickMailJob;
    private string? busySetupZone;

    [SupplyParameterFromQuery(Name = "tab")]
    public string? Tab { get; set; }

    protected override void OnParametersSet()
    {
        if (Tab is OnDemandAppsTabValue or PublishedAppsTabValue or SetupTabValue or DiagnosticsTabValue or CaddyTabValue or MailRelayTabValue)
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
                    true, HelpUrl: "/edge-gateway?tab=mail-relay");
                return;
            }
            quickMailJob = await MailRelayService.StartProvisioningAsync(preview.Request);
            zoneSetupMessages[domain.DomainName] = new(string.Join(" ", preview.Warnings));
        }
        catch (Exception exception)
        {
            zoneSetupMessages[domain.DomainName] = new(exception.Message, true, HelpUrl: "/edge-gateway?tab=mail-relay");
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
