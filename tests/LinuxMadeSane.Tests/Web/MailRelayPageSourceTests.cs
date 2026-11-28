// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Tests.Web;

public sealed class MailRelayPageSourceTests
{
    [Fact]
    public void EdgeGateway_HasDedicatedMailRelayTab()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/EdgeGateway.razor");

        Assert.Contains("<TabPage Value=\"@MailRelayTabValue\" Title=\"Mail Relay\">", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SMTP Server\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Email Server\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_EnforcesEdgeGatewayPrerequisiteWithoutDuplicatingCloudflareSecrets()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");

        Assert.Contains("Direct SMTP requires Edge Gateway DNS setup.", source, StringComparison.Ordinal);
        Assert.Contains("Cloudflare connection", source, StringComparison.Ordinal);
        Assert.Contains("DNS access", source, StringComparison.Ordinal);
        Assert.Contains("Href=\"/edge-gateway\"", source, StringComparison.Ordinal);
        Assert.Contains("Zone → DNS → Edit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("API token", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiTokenSecretReference", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_ReusesCloudflareServicesAndOnlyWritesForExplicitPermissionTest()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayPreflightService.cs");

        Assert.Contains("ICloudflareExposureStore cloudflareStore", source, StringComparison.Ordinal);
        Assert.Contains("ICloudflareZoneService cloudflareZoneService", source, StringComparison.Ordinal);
        Assert.Contains("ICloudflareDnsService cloudflareDnsService", source, StringComparison.Ordinal);
        Assert.Contains("ISecretStore secretStore", source, StringComparison.Ordinal);
        Assert.Contains("ILinuxCommandRunner commandRunner", source, StringComparison.Ordinal);
        Assert.Contains("verifyDnsEdit", source, StringComparison.Ordinal);
        Assert.Contains("CreateRecordAsync", source, StringComparison.Ordinal);
        Assert.Contains("DeleteRecordAsync", source, StringComparison.Ordinal);
        Assert.Contains("Proxied", ReadRepoFile("src/LinuxMadeSane.Core/Models/Cloudflare/CloudflareDnsRecord.cs"), StringComparison.Ordinal);
        Assert.Contains("DNS Edit was verified by creating", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_ProvidesAReviewedRealProvisioningAction()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");

        Assert.Contains("Review setup", source, StringComparison.Ordinal);
        Assert.Contains("Install Mail Relay", source, StringComparison.Ordinal);
        Assert.Contains("Set up with selected domain", source, StringComparison.Ordinal);
        Assert.Contains("MailRelayService.StartProvisioningAsync", source, StringComparison.Ordinal);
        Assert.Contains("Apply these DNS and server changes", source, StringComparison.Ordinal);
        Assert.Contains("Setup continues in the background", source, StringComparison.Ordinal);
        Assert.Contains("Sending domains", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_KeepsDiagnosticsSecondaryAndExplainsCredentialReplacement()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");

        Assert.Contains("<details class=\"surface-card mail-relay-checks\">", source, StringComparison.Ordinal);
        Assert.Contains("Saved logins are encrypted in Credentials.", source, StringComparison.Ordinal);
        Assert.Contains("Copy SMTP password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_ExplainsThatExistingMailProvidersArePreservedAndSpfIsMerged()
    {
        var page = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");
        var provisioner = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayProvisioningService.cs");

        Assert.Contains("Preserve + merge", page, StringComparison.Ordinal);
        Assert.Contains("Microsoft 365 detected", page, StringComparison.Ordinal);
        Assert.Contains("Incoming email / MX", page, StringComparison.Ordinal);
        Assert.Contains("UNCHANGED", page, StringComparison.Ordinal);
        Assert.Contains("Existing SPF", page, StringComparison.Ordinal);
        Assert.Contains("Proposed SPF", page, StringComparison.Ordinal);
        Assert.Contains("Sent Items", page, StringComparison.Ordinal);
        Assert.Contains("DMARC identity alignment", page, StringComparison.Ordinal);
        Assert.Contains("header_checks = pcre:/etc/postfix/header_checks.pcre", provisioner, StringComparison.Ordinal);
        Assert.DoesNotContain("UpsertRecordAsync(token, request.CloudflareZoneId, currentRecords, \"MX\"", provisioner, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_SendsARealTestThroughTheSelectedMailRelayApplication()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");

        Assert.Contains("Send a test email", source, StringComparison.Ordinal);
        Assert.Contains("Label=\"@(isSendingTest ? \"Sending and checking delivery…\" : \"Send Test\")\"", source, StringComparison.Ordinal);
        Assert.Contains("MailRelayService.SendTestAsync", source, StringComparison.Ordinal);
        Assert.Contains("Selected user policy", source, StringComparison.Ordinal);
        Assert.Contains("Accepted by relay", source, StringComparison.Ordinal);
        Assert.Contains("Queue ID", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@bind-Value=\"testPassword\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("string? testPassword", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_ListsConfiguredDomainsAndLoadsTheirSettingsForEditing()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");

        Assert.Contains("Label=\"Domain\"", source, StringComparison.Ordinal);
        Assert.Contains("Edit configured domain", source, StringComparison.Ordinal);
        Assert.Contains("ConfigureSelectedZoneAsync", source, StringComparison.Ordinal);
        Assert.Contains("markers.Add(\"configured\")", source, StringComparison.Ordinal);
        Assert.Contains("@if (setupResult?.Success != true)", source, StringComparison.Ordinal);
        Assert.Contains("Label=\"Done\"", source, StringComparison.Ordinal);
        Assert.Contains("Edit settings", source, StringComparison.Ordinal);
        Assert.Contains("EditDomainAsync(domain)", source, StringComparison.Ordinal);
        Assert.Contains("item.ZoneId.Equals(domain.CloudflareZoneId", source, StringComparison.Ordinal);
        Assert.Contains("id=\"mail-relay-setup\"", source, StringComparison.Ordinal);
        Assert.Contains("<UiModal IsOpen=\"@domainEditorOpen\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Href=\"#mail-relay-setup\"", source, StringComparison.Ordinal);
        Assert.Contains("DomainDnsLabel(domain.SpfStatus, \"SPF\")", source, StringComparison.Ordinal);
        Assert.Contains("DomainDnsLabel(domain.DkimStatus, \"DKIM\")", source, StringComparison.Ordinal);
        Assert.Contains("DomainDnsLabel(domain.DmarcStatus, \"DMARC\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_UsesCompactAlignedRowsInsteadOfStackedDomainAndUserCards()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");
        var modalStyles = ReadRepoFile("src/LinuxMadeSane.Web/Components/Ui/UiModal.razor.css");

        Assert.Contains("Label=\"Domain\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Label=\"Working Cloudflare zone\"", source, StringComparison.Ordinal);
        Assert.Contains("class=\"inventory-table mail-relay-domain-list\"", source, StringComparison.Ordinal);
        Assert.Contains("class=\"inventory-table mail-relay-client-list\"", source, StringComparison.Ordinal);
        Assert.Contains("Class=\"mail-relay-client-modal\"", source, StringComparison.Ordinal);
        Assert.Contains("</section>\n\n            @if (clientEditorOpen)", source, StringComparison.Ordinal);
        Assert.Contains("var(--color-base-900)", modalStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("background: var(--surface-card);", modalStyles, StringComparison.Ordinal);
        Assert.Contains("class=\"surface-card mail-relay-maintenance\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-relay-domain-footer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-relay-client-footer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_ManagesPostfixUsersPasswordsAndSenderDomains()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");

        Assert.Contains("SMTP users", source, StringComparison.Ordinal);
        Assert.Contains("Add SMTP User", source, StringComparison.Ordinal);
        Assert.Contains("Generate secure password", source, StringComparison.Ordinal);
        Assert.Contains("Copy password", source, StringComparison.Ordinal);
        Assert.Contains("Show password", source, StringComparison.Ordinal);
        Assert.Contains("Allowed sender domains", source, StringComparison.Ordinal);
        Assert.Contains("MailRelayService.SaveClientAsync", source, StringComparison.Ordinal);
        Assert.Contains("Enter your own or generate one below.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_OffersARestrictedUnauthenticatedListenerForLegacyDevices()
    {
        var page = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");
        var provisioner = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayProvisioningService.cs");

        Assert.Contains("Edit interfaces", page, StringComparison.Ordinal);
        Assert.Contains("Allow legacy TCP/25 submission", page, StringComparison.Ordinal);
        Assert.Contains("Listen network interfaces", page, StringComparison.Ordinal);
        Assert.Contains("UiNetworkInterfaceIpPicker", page, StringComparison.Ordinal);
        Assert.Contains("Allowed devices or networks", page, StringComparison.Ordinal);
        Assert.Contains("100.64.0.0/10", page, StringComparison.Ordinal);
        Assert.Contains("Public interface selected", page, StringComparison.Ordinal);
        Assert.Contains("Caddy TCP/layer-4 forwarder", page, StringComparison.Ordinal);
        Assert.Contains("MailRelayService.ConfigureLegacySubmissionAsync", page, StringComparison.Ordinal);
        Assert.Contains("smtpd_sasl_auth_enable=no", provisioner, StringComparison.Ordinal);
        Assert.Contains("smtpd_client_restrictions=check_client_access,cidr:/etc/postfix/legacy_clients.cidr,reject", provisioner, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0/0 REJECT Legacy SMTP source is not allowed", provisioner, StringComparison.Ordinal);
        Assert.Contains("all-interface address 0.0.0.0 is not allowed", provisioner, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_OffersScheduledDynamicPublicIpProtectionWithoutTouchingProviderMailDns()
    {
        var page = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");
        var monitor = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayPublicIpMonitorService.cs");
        var provisioner = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayProvisioningService.cs");

        Assert.Contains("Public IP protection", page, StringComparison.Ordinal);
        Assert.Contains("Automatically detect and repair public IP changes", page, StringComparison.Ordinal);
        Assert.Contains("Check interval (minutes)", page, StringComparison.Ordinal);
        Assert.Contains("Static-IP servers can leave this off", page, StringComparison.Ordinal);
        Assert.Contains("MailRelayService.CheckPublicIpNowAsync", page, StringComparison.Ordinal);
        Assert.Contains("PeriodicTimer", monitor, StringComparison.Ordinal);
        Assert.Contains("RemoveLmsSpfIpv4Authorization(baseValue, previousPublicIp)", provisioner, StringComparison.Ordinal);
        Assert.Contains("The existing shared DMARC policy is present and unchanged.", provisioner, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateRecordAsync(apiToken, domain.CloudflareZoneId, current with { Type = \"TXT\", Content = expected", provisioner, StringComparison.Ordinal);
    }

    [Fact]
    public void Removal_SubtractsOnlyLmsOwnedDnsAndRequiresExplicitConfirmation()
    {
        var page = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");
        var provisioner = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayProvisioningService.cs");

        Assert.Contains("Remove Mail Relay", page, StringComparison.Ordinal);
        Assert.Contains("Remove LMS-managed DNS changes", page, StringComparison.Ordinal);
        Assert.Contains("I understand these selected Mail Relay items will be removed", page, StringComparison.Ordinal);
        Assert.Contains("MailRelayService.RemoveAsync", page, StringComparison.Ordinal);
        Assert.Contains("RemoveLmsSpfIpv4Authorization", provisioner, StringComparison.Ordinal);
        Assert.Contains("its value changed after LMS created it, so LMS preserved it", provisioner, StringComparison.Ordinal);
        Assert.Contains("Existing MX, provider DKIM and DMARC records were preserved", provisioner, StringComparison.Ordinal);
    }

    [Fact]
    public void Provisioner_UsesExistingInfrastructureAndBuildsOutboundOnlyRelay()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayProvisioningService.cs");

        Assert.Contains("ICloudflareDnsService cloudflareDnsService", source, StringComparison.Ordinal);
        Assert.Contains("ISecretStore secretStore", source, StringComparison.Ordinal);
        Assert.Contains("ILinuxCommandRunner commandRunner", source, StringComparison.Ordinal);
        Assert.Contains("postfix postfix-pcre opendkim", source, StringComparison.Ordinal);
        Assert.Contains("smtpd_tls_auth_only = yes", source, StringComparison.Ordinal);
        Assert.Contains("smtpd_sasl_local_domain = $myhostname", source, StringComparison.Ordinal);
        Assert.Contains("/var/spool/postfix/etc/$resolver_file", source, StringComparison.Ordinal);
        Assert.Contains("resolv.conf hosts host.conf nsswitch.conf services protocols", source, StringComparison.Ordinal);
        Assert.Contains("reject_unauth_destination", source, StringComparison.Ordinal);
        Assert.Contains("TestOpenRelayRejectedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dovecot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("roundcube", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DomainPicker_RendersEveryZoneReturnedByEdgeGateway()
    {
        var source = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");

        Assert.Contains("dashboard.Preflight.AvailableZones", source, StringComparison.Ordinal);
        Assert.Contains("@foreach (var zone in dashboard.Preflight.AvailableZones)", source, StringComparison.Ordinal);
        Assert.Contains("ValueChanged=\"SelectCloudflareZoneAsync\"", source, StringComparison.Ordinal);
        Assert.Contains("MailRelayService.InspectPreflightAsync(selectedCloudflareZoneId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AvailableZones.First", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_BlocksRelayHostnameCollisionsInsteadOfTakingOverExistingDns()
    {
        var page = ReadRepoFile("src/LinuxMadeSane.Web/Components/Pages/MailRelayPanel.razor");
        var provisioner = ReadRepoFile("src/LinuxMadeSane.Infrastructure/Services/MailRelayProvisioningService.cs");

        Assert.Contains("Label=\"Relay hostname\"", page, StringComparison.Ordinal);
        Assert.Contains("LMS did not create it and will not replace or reuse it", provisioner, StringComparison.Ordinal);
        Assert.Contains("already has an IPv6 address", provisioner, StringComparison.Ordinal);
        Assert.Contains("isInitialRelaySetup", provisioner, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
