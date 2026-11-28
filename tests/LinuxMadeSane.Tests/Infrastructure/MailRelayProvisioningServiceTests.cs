// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.MailRelay;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Infrastructure.Services;

using LinuxMadeSane.Infrastructure.Services.Cloudflare;

namespace LinuxMadeSane.Tests.Infrastructure;

public sealed class MailRelayProvisioningServiceTests
{
    [Fact]
    public async Task ApiPreviewDoesNotRequireDirectMxDnsOrPublicIpv4()
    {
        var preflight = BuildPreflight(DateTimeOffset.UtcNow);
        preflight = preflight with { PublicIpAddress = "", Checks = preflight.Checks.Select(c => c.Key == MailRelayPreflightCheckKeys.Docker ? c : c with { State = MailRelayPreflightCheckState.Failed }).ToArray() };
        var request = Request() with { CloudflareZoneId = "", Outbound = new() {
            Mode = MailRelayOutboundMode.MicrosoftGraph, Api = new() { SenderAddress = "lms@example.test",
                TenantId = "00000000-0000-0000-0000-000000000001", ClientId = "00000000-0000-0000-0000-000000000002", ClientSecretReference = "secret://client" } } };
        var preview = await CreateService([], preflight: preflight).PreviewAsync(request);
        Assert.True(preview.CanInstall); Assert.Empty(preview.Errors); Assert.Empty(preview.DnsChanges);
        Assert.False(preview.Preflight.CloudflareDnsReady); Assert.True(preview.Preflight.HttpsDelivery);
        Assert.Equal(443, preview.Request.Outbound!.DestinationPort);
    }
    [Fact]
    public async Task Preview_DoesNotEnableDeviceSubmissionWithoutAnExplicitSourceAllowlist()
    {
        var preview = await CreateService([]).PreviewAsync(Request() with { LocalDevices = new(true, ["10.0.0.4"], []) });
        Assert.False(preview.CanInstall);
        Assert.Contains(preview.Errors, e => e.Contains("allowed device IP"));
    }

    [Fact]
    public void DevicePort25AndAuthenticatedUpstream587AreIndependentPolicies()
    {
        var configuration = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with {
            AllowLegacyPort25 = true, LegacyListenAddresses = ["10.0.0.4"], LegacyAllowedNetworks = ["10.0.0.40"],
            DeliveryMode = MailRelayDeliveryMode.UpstreamSmtp, Outbound = MailRelayOutboundTests.Settings() };
        var listeners = MailRelayProvisioningService.BuildMasterCf(["127.0.0.1"], configuration);
        Assert.Contains("10.0.0.4:25 inet", listeners);
        Assert.Contains("smtpd_sasl_auth_enable=no", listeners);
        Assert.Contains("127.0.0.1:587 inet", listeners);
        Assert.Contains("smtpd_sasl_auth_enable=yes", listeners);
        Assert.Contains("relayhost = [smtp.provider.test]:587", MailRelayProvisioningService.BuildMainCf(configuration));
    }

    [Fact]
    public async Task Provision_DoesNotClaimANewRouteIsInstalledWhenPreparationFails()
    {
        var configuration = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with { Enabled = true, RelayHostname = "smtp.example.test" };
        var store = new StubMailRelayStore(configuration);
        var outbound = MailRelayOutboundTests.Service(new MailRelayOutboundTests.Probe(SmtpSubmissionProbeStatus.Verified));
        var service = CreateService([], store: store, outbound: outbound);
        var result = await service.ProvisionAsync(Request() with { Outbound = MailRelayOutboundTests.Settings() });
        Assert.False(result.Success); // This fixture deliberately rejects all system commands.
        Assert.Equal(MailRelayDeliveryMode.DirectInternet, store.SavedConfiguration!.DeliveryMode);
        Assert.Equal(configuration.Outbound, store.SavedConfiguration.Outbound);
    }

    [Fact]
    public async Task Preview_VerifiedUpstreamAllowsSetupWhenAzureBlocks25AndMergesProviderSpf()
    {
        var preflight = BuildPreflight(DateTimeOffset.UtcNow);
        preflight = preflight with { Checks = preflight.Checks.Select(c => c.Key == MailRelayPreflightCheckKeys.OutboundSmtp ? c with { State = MailRelayPreflightCheckState.Failed } : c).ToArray() };
        var outbound = MailRelayOutboundTests.Service(new MailRelayOutboundTests.Probe(SmtpSubmissionProbeStatus.Verified));
        var service = CreateService([Record("spf", "example.test", "TXT", "v=spf1 include:existing.test ~all")], preflight: preflight, outbound: outbound);
        var preview = await service.PreviewAsync(Request() with { Outbound = MailRelayOutboundTests.Settings() with { SpfInclude = "_spf.provider.test" } });
        Assert.True(preview.CanInstall, string.Join("; ", preview.Errors));
        Assert.True(preview.UseUpstream);
        Assert.Equal(MailRelayDeliveryMode.UpstreamSmtp, preview.ExistingEmailConfiguration.DeliveryMode);
        var spf = Assert.Single(preview.DnsChanges, c => c.Purpose == "SPF");
        Assert.Contains("include:_spf.provider.test", spf.ProposedValue);
        Assert.Contains("include:existing.test", spf.ProposedValue);
        Assert.EndsWith("~all", spf.ProposedValue);
        Assert.Contains(preview.Warnings, w => w.Contains("authorise your sender/domain"));
    }

    [Fact]
    public async Task Preview_ChangingAvailableRouteAfterReviewRequiresANewReview()
    {
        var outbound = MailRelayOutboundTests.Service(new MailRelayOutboundTests.Probe(SmtpSubmissionProbeStatus.Unreachable));
        var service = CreateService([], outbound: outbound);
        var preview = await service.PreviewAsync(Request() with { Outbound = MailRelayOutboundTests.Settings(), ReviewedDeliveryMode = MailRelayDeliveryMode.UpstreamSmtp });
        Assert.False(preview.CanInstall);
        Assert.Contains(preview.Errors, error => error.Contains("route changed after review"));
    }

    [Theory]
    [InlineData("v=spf1 include:_spf.google.com ~all")]
    [InlineData("\"v=spf1 include:_spf.google.com ~all\"")]
    [InlineData("\"v=sp\" \"f1 include:_spf.google.com ~all\"")]
    public async Task Preview_PreservesExistingSpfMechanismsAndAddsRelayIp(string apiContent)
    {
        var service = CreateService([
            new CloudflareDnsRecordDto
            { Id = "spf", Name = "example.test", Type = "TXT", Content = apiContent }.ToModel("zone"),
            Record("unrelated", "example.test", "TXT", "v=spf10 other-version")
        ]);

        var preview = await service.PreviewAsync(Request());

        Assert.True(preview.CanInstall);
        var spf = Assert.Single(preview.DnsChanges, item => item.Purpose == "SPF");
        Assert.Equal(MailRelaySetupChangeKind.Update, spf.Kind);
        Assert.Equal("v=spf1 ip4:203.0.113.10 include:_spf.google.com ~all", spf.ProposedValue);
    }

    [Fact]
    public async Task Preview_KeepsTheExistingProviderRecordOnRepeatSetup()
    {
        var existing = new CloudflareDnsRecordDto
        {
            Id = "provider-spf", Name = "example.test", Type = "TXT",
            Content = "\"v=spf1 ip4:203.0.113.10 include:spf.example.net ~all\""
        }.ToModel("zone");
        var preview = await CreateService([existing]).PreviewAsync(Request());
        var spf = Assert.Single(preview.DnsChanges, change => change.Purpose == "SPF");
        Assert.Equal(MailRelaySetupChangeKind.Keep, spf.Kind);
        Assert.Equal(existing.Content, spf.ProposedValue);
    }

    [Fact]
    public async Task Preview_BlocksMultipleSpfRecordsInsteadOfOverwritingThem()
    {
        var service = CreateService([
            new CloudflareDnsRecordDto { Id = "spf-one", Name = "example.test", Type = "TXT", Content = "\"v=spf1 include:_spf.google.com ~all\"" }.ToModel("zone"),
            Record("spf-two", "example.test", "TXT", "v=spf1 ip4:192.0.2.2 -all")
        ]);

        var preview = await service.PreviewAsync(Request());

        Assert.False(preview.CanInstall);
        Assert.Contains(preview.Errors, error => error.Contains("multiple SPF records", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_RefusesRelayHostnameOutsideSelectedZone()
    {
        var service = CreateService([]);

        var preview = await service.PreviewAsync(Request() with { RelayHostname = "smtp.somewhere-else.test" });

        Assert.False(preview.CanInstall);
        Assert.Contains(preview.Errors, error => error.Contains("inside the selected Cloudflare zone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_BlocksAnExistingRelayAddressThatLmsDoesNotOwn()
    {
        var service = CreateService([
            Record("existing-a", "smtp.example.test", "A", "203.0.113.10")
        ]);

        var preview = await service.PreviewAsync(Request());

        Assert.False(preview.CanInstall);
        Assert.Contains(preview.Errors, error =>
            error.Contains("LMS did not create it", StringComparison.Ordinal) &&
            error.Contains("Choose an unused relay hostname", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_KeepsAnExistingRelayAddressMarkedAsLmsManaged()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now);
        var domain = new MailRelayDomain(
            Guid.NewGuid(), configuration.Id, "zone", "example.test", false, "lms2026", null, null, null,
            null, null, null, null, null, null, null, null,
            MailRelayDnsStatus.NotChecked, MailRelayDnsStatus.NotChecked, MailRelayDnsStatus.NotChecked,
            MailRelayDmarcPolicy.Monitor, null, now, now);
        var tracked = new MailRelayDnsRecord(
            Guid.NewGuid(), domain.Id, "lms-a", "A", "smtp.example.test", "Relay hostname", true, false,
            null, "203.0.113.10", MailRelayDnsChangeType.Created, now, now);
        var service = CreateService([
            Record("lms-a", "smtp.example.test", "A", "203.0.113.10", "Managed by Linux Made Sane Mail Relay")
        ], new StubMailRelayStore(configuration, [domain], [tracked]));

        var preview = await service.PreviewAsync(Request());

        Assert.True(preview.CanInstall);
        var address = Assert.Single(preview.DnsChanges, item => item.Purpose == "Relay hostname");
        Assert.Equal(MailRelaySetupChangeKind.Keep, address.Kind);
    }

    [Fact]
    public async Task Preview_ConfiguresASecondZoneWithoutMovingTheExistingRelayHostname()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            RelayHostname = "smtp.example.test",
            PublicIpAddress = "203.0.113.10"
        };
        var store = new StubMailRelayStore(configuration);
        var preflight = BuildPreflight(now, zoneId: "zone-two", zoneName: "second.test");
        var service = CreateService([], store, preflight);

        var preview = await service.PreviewAsync(Request() with
        {
            CloudflareZoneId = "zone-two",
            RelayHostname = "smtp.example.test",
            SendingDomain = "second.test"
        });

        Assert.True(preview.CanInstall);
        Assert.DoesNotContain(preview.DnsChanges, item => item.Purpose == "Relay hostname");
        Assert.Contains(preview.DnsChanges, item => item.Purpose == "SPF" && item.RecordName == "second.test");
    }

    [Fact]
    public async Task Preview_DetectsMicrosoft365AndPreservesItsMailConfiguration()
    {
        var service = CreateService([
            Record("mx", "example.test", "MX", "example-test.mail.protection.outlook.com"),
            Record("spf", "example.test", "TXT", "v=spf1 include:spf.protection.outlook.com -all"),
            Record("dkim-one", "selector1._domainkey.example.test", "CNAME", "selector1-example._domainkey.tenant.onmicrosoft.com"),
            Record("dkim-two", "selector2._domainkey.example.test", "CNAME", "selector2-example._domainkey.tenant.onmicrosoft.com"),
            Record("dmarc", "_dmarc.example.test", "TXT", "v=DMARC1; p=reject; rua=mailto:dmarc@example.test")
        ]);

        var preview = await service.PreviewAsync(Request());

        Assert.True(preview.CanInstall);
        var existing = preview.ExistingEmailConfiguration;
        Assert.Equal(MailRelayExistingProvider.Microsoft365, existing.Provider);
        Assert.Equal(["example-test.mail.protection.outlook.com"], existing.MxRecords);
        Assert.Equal("v=spf1 include:spf.protection.outlook.com -all", existing.ExistingSpf);
        Assert.Equal("v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com -all", existing.ProposedSpf);
        Assert.Equal(2, existing.ExistingDkimRecords.Count);
        Assert.Equal("REJECT", existing.ExistingDmarcPolicy);
        Assert.DoesNotContain(preview.DnsChanges, change => change.RecordType.Equals("MX", StringComparison.OrdinalIgnoreCase));
        var dmarc = Assert.Single(preview.DnsChanges, change => change.Purpose == "DMARC");
        Assert.Equal(MailRelaySetupChangeKind.Keep, dmarc.Kind);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:dmarc@example.test", dmarc.ProposedValue);
    }

    [Fact]
    public async Task Preview_DetectsGoogleWorkspaceWithoutRequiringProviderRecognition()
    {
        var google = CreateService([
            Record("mx", "example.test", "MX", "aspmx.l.google.com"),
            Record("spf", "example.test", "TXT", "v=spf1 include:_spf.google.com ~all")
        ]);
        var custom = CreateService([
            Record("mx", "example.test", "MX", "mail.provider.invalid"),
            Record("spf", "example.test", "TXT", "v=spf1 a mx ?all")
        ]);

        Assert.Equal(MailRelayExistingProvider.GoogleWorkspace, (await google.PreviewAsync(Request())).ExistingEmailConfiguration.Provider);
        var customPreview = await custom.PreviewAsync(Request());
        Assert.True(customPreview.CanInstall);
        Assert.Equal(MailRelayExistingProvider.ExistingMailProvider, customPreview.ExistingEmailConfiguration.Provider);
    }

    [Fact]
    public async Task Preview_BlocksMalformedSpfAndUnownedDkimSelectorCollisions()
    {
        var malformedSpf = CreateService([
            Record("spf", "example.test", "TXT", "v=spf1 include: -all")
        ]);
        var dkimCollision = CreateService([
            Record("dkim", "lms2026._domainkey.example.test", "TXT", "v=DKIM1; p=someone-elses-key")
        ]);

        var spfPreview = await malformedSpf.PreviewAsync(Request());
        Assert.False(spfPreview.CanInstall);
        Assert.Contains(spfPreview.Errors, error => error.Contains("not safe to modify", StringComparison.Ordinal));

        var dkimPreview = await dkimCollision.PreviewAsync(Request());
        Assert.False(dkimPreview.CanInstall);
        Assert.Contains(dkimPreview.Errors, error => error.Contains("already belongs to another mail system", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_PreservesAllExistingSpfTermsAndFinalPolicy()
    {
        const string existing = "v=spf1 ip4:192.0.2.1 ip6:2001:db8::/32 a mx include:_spf.example.test exists:%{i}.spf.example.test redirect=redirect.example.test ~all";
        var service = CreateService([Record("spf", "example.test", "TXT", existing)]);

        var preview = await service.PreviewAsync(Request());

        Assert.True(preview.CanInstall);
        Assert.Equal(
            "v=spf1 ip4:203.0.113.10 ip4:192.0.2.1 ip6:2001:db8::/32 a mx include:_spf.example.test exists:%{i}.spf.example.test redirect=redirect.example.test ~all",
            preview.ExistingEmailConfiguration.ProposedSpf);
        Assert.Equal(5, preview.ExistingEmailConfiguration.SpfDnsLookupTerms);
    }

    [Fact]
    public async Task Provision_SavesRelayAndDomainDraftBeforeDockerInstallation()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new StubMailRelayStore();
        var preflight = BuildPreflight(now, new MailRelayPreflightCheck(
            MailRelayPreflightCheckKeys.Docker,
            "Docker",
            MailRelayPreflightCheckState.Warning,
            "INSTALL REQUIRED",
            "Docker will be installed."));
        var service = new MailRelayProvisioningService(
            new StubPreflightService(preflight),
            new StubExposureStore(new CloudflareSettings(
                AiLocalMachine.ManagedHostId, "account", "Account", "zone", "example.test", "secret://cloudflare", now, now)),
            new StubDnsService([]),
            new StubSecretStore(),
            store,
            new FailingPackageManagementService(),
            new RejectingServiceManagementService(),
            new RejectingCommandRunner());
        var progress = new List<MailRelaySetupProgressUpdate>();

        var result = await service.ProvisionAsync(Request(), new InlineProgress(progress.Add));

        Assert.False(result.Success);
        Assert.NotNull(store.SavedConfiguration);
        Assert.Equal("smtp.example.test", store.SavedConfiguration.RelayHostname);
        var domain = Assert.Single(store.SavedDomains);
        Assert.Equal("example.test", domain.DomainName);
        Assert.False(domain.Enabled);
        Assert.Contains(progress, item => item.Key == "draft" && item.State == MailRelaySetupStepState.Complete);
        Assert.Contains("simulated package failure", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MasterConfiguration_SeparatesAuthenticatedAndLegacyListeners()
    {
        var configuration = MailRelayConfiguration.CreateDefault(DateTimeOffset.UtcNow) with
        {
            RelayHostname = "smtp.example.test",
            AllowLegacyPort25 = true,
            LegacyListenAddresses = ["192.168.50.10"],
            LegacyAllowedNetworks = ["192.168.50.40", "192.168.50.64/28"]
        };

        var master = MailRelayProvisioningService.BuildMasterCf(
            ["127.0.0.1", "100.64.12.3"],
            configuration);

        Assert.Contains("127.0.0.1:587 inet", master, StringComparison.Ordinal);
        Assert.Contains("100.64.12.3:587 inet", master, StringComparison.Ordinal);
        Assert.Contains("smtpd_tls_security_level=encrypt", master, StringComparison.Ordinal);
        Assert.Contains("smtpd_relay_restrictions=permit_sasl_authenticated,reject", master, StringComparison.Ordinal);
        Assert.Contains("192.168.50.10:25 inet", master, StringComparison.Ordinal);
        Assert.Contains("smtpd_sasl_auth_enable=no", master, StringComparison.Ordinal);
        Assert.Contains("mynetworks=192.168.50.40,192.168.50.64/28", master, StringComparison.Ordinal);
        Assert.Contains("smtpd_client_restrictions=check_client_access,cidr:/etc/postfix/legacy_clients.cidr,reject", master, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0:25", master, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMaps_AllowOnlyConfiguredSourcesAndSendingDomains()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now);
        var domain = new MailRelayDomain(
            Guid.NewGuid(), configuration.Id, "zone", "example.test", true, "lms2026", null, null, null,
            null, null, null, null, null, null, null, null,
            MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass,
            MailRelayDmarcPolicy.Monitor, null, now, now);

        var clients = MailRelayProvisioningService.BuildLegacyClientAccess(["192.168.50.40"], ["192.168.50.40"]);
        Assert.StartsWith("192.168.50.40 REJECT Blocked SMTP source\n192.168.50.40 OK", clients);
        var senders = MailRelayProvisioningService.BuildLegacySenderAccess([domain]);
        var headers = MailRelayProvisioningService.BuildHeaderChecks([domain]);

        Assert.Contains("192.168.50.40 OK", clients, StringComparison.Ordinal);
        Assert.EndsWith("0.0.0.0/0 REJECT Legacy SMTP source is not allowed\n", clients, StringComparison.Ordinal);
        Assert.Contains("/^.+@example\\.test$/i OK", senders, StringComparison.Ordinal);
        Assert.EndsWith("/.*/ REJECT Sender domain is not configured in LMS Mail Relay\n", senders, StringComparison.Ordinal);
        Assert.Contains("<[^<>@\\r\\n]+@example\\.test>", headers, StringComparison.Ordinal);
        Assert.Contains("[^<>,@[:space:]]+@example\\.test", headers, StringComparison.Ordinal);
        Assert.EndsWith("/^From:/i REJECT Header From domain is not configured for LMS DKIM signing\n", headers, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySettings_NormalizeSourcesAllowExplicitPublicInterfacesButRejectOpenAccess()
    {
        var normalized = MailRelayProvisioningService.NormalizeLegacyRequest(
            new MailRelayLegacySubmissionRequest(
                true,
                ["192.168.50.10", "203.0.113.10"],
                ["192.168.50.77/28", "100.64.12.3", "198.51.100.24"]));

        Assert.Equal(["192.168.50.10", "203.0.113.10"], normalized.ListenAddresses);
        Assert.Equal(["192.168.50.64/28", "100.64.12.3", "198.51.100.24"], normalized.AllowedNetworks);
        var error = Assert.Throws<InvalidOperationException>(() =>
            MailRelayProvisioningService.NormalizeLegacyRequest(
                new MailRelayLegacySubmissionRequest(true, ["0.0.0.0"], ["0.0.0.0/0"])));
        Assert.Contains("all-interface address 0.0.0.0 is not allowed", error.Message, StringComparison.Ordinal);

        var broadPublicSource = Assert.Throws<InvalidOperationException>(() =>
            MailRelayProvisioningService.NormalizeLegacyRequest(
                new MailRelayLegacySubmissionRequest(true, ["203.0.113.10"], ["198.51.0.0/16"])));
        Assert.Contains("too broad", broadPublicSource.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Removal_StripsOnlyTheExactLmsSpfMechanismFromTheCurrentRecord()
    {
        const string current = "v=spf1 ip4:203.0.113.10/32 ip4:192.0.2.4 include:spf.protection.outlook.com exists:new.provider.test ~all";

        var result = MailRelayProvisioningService.RemoveLmsSpfIpv4Authorization(current, "203.0.113.10");

        Assert.Equal(
            "v=spf1 ip4:192.0.2.4 include:spf.protection.outlook.com exists:new.provider.test ~all",
            result);
    }

    [Fact]
    public async Task PublicIpSync_UpdatesManagedAddressAndOnlyLmsSpfTermWhilePreservingProviderMailDns()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            RelayHostname = "smtp.example.test",
            PublicIpAddress = "203.0.113.10",
            MonitorPublicIpChanges = true
        };
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var privateKey = rsa.ExportRSAPrivateKeyPem();
        var publicDkim = $"v=DKIM1; k=rsa; p={Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}";
        var domain = new MailRelayDomain(
            Guid.NewGuid(), configuration.Id, "zone", "example.test", true, "lms2026", "secret://dkim", now, now,
            null, null, null, null, null, "lms-dkim", "shared-spf", "provider-dmarc",
            MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass,
            MailRelayDmarcPolicy.Reject, null, now, now);
        var records = new[]
        {
            Record("provider-mx", "example.test", "MX", "example-test.mail.protection.outlook.com"),
            Record("lms-a", "smtp.example.test", "A", "203.0.113.10", "Managed by Linux Made Sane Mail Relay"),
            new CloudflareDnsRecordDto { Id = "shared-spf", Name = "example.test", Type = "TXT", Content = "\"v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com -all\"" }.ToModel("zone"),
            Record("lms-dkim", "lms2026._domainkey.example.test", "TXT", publicDkim, "Managed by Linux Made Sane Mail Relay"),
            Record("provider-dmarc", "_dmarc.example.test", "TXT", "v=DMARC1; p=reject")
        };
        var tracked = new[]
        {
            new MailRelayDnsRecord(Guid.NewGuid(), domain.Id, "lms-a", "A", "smtp.example.test", "Relay hostname", true, false, null, "203.0.113.10", MailRelayDnsChangeType.Created, now, now),
            new MailRelayDnsRecord(Guid.NewGuid(), domain.Id, "shared-spf", "TXT", "example.test", "SPF", false, true,
                "v=spf1 include:spf.protection.outlook.com -all",
                "v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com -all",
                MailRelayDnsChangeType.ModifiedShared, now, now),
            new MailRelayDnsRecord(Guid.NewGuid(), domain.Id, "lms-dkim", "TXT", "lms2026._domainkey.example.test", "DKIM", true, false, null, publicDkim, MailRelayDnsChangeType.Created, now, now),
            new MailRelayDnsRecord(Guid.NewGuid(), domain.Id, "provider-dmarc", "TXT", "_dmarc.example.test", "DMARC", false, false, "v=DMARC1; p=reject", "v=DMARC1; p=reject", MailRelayDnsChangeType.ObservedExisting, now, now)
        };
        var store = new StubMailRelayStore(configuration, [domain], tracked);
        var dns = new StubDnsService(records);
        var service = new MailRelayProvisioningService(
            new StubPreflightService(BuildPreflight(now)),
            new StubExposureStore(new CloudflareSettings(
                AiLocalMachine.ManagedHostId, "account", "Account", "zone", "example.test", "secret://cloudflare", now, now)),
            dns,
            new StubSecretStore(privateKey),
            store,
            new RejectingPackageManagementService(),
            new RejectingServiceManagementService(),
            new DnsLookupMissCommandRunner());

        var result = await service.SynchronizePublicIpAsync("203.0.113.20");

        Assert.True(result.Success);
        Assert.True(result.PublicIpChanged);
        Assert.Equal("203.0.113.20", Assert.IsType<MailRelayConfiguration>(store.SavedConfiguration).PublicIpAddress);
        Assert.Contains(dns.Records, item => item.Id == "lms-a" && item.Content == "203.0.113.20" && !item.Proxied);
        Assert.Contains(dns.Records, item => item.Id == "shared-spf" &&
            item.Content == "v=spf1 ip4:203.0.113.20 include:spf.protection.outlook.com -all");
        Assert.DoesNotContain(dns.Records, item => item.Id == "shared-spf" && item.Content.Contains("203.0.113.10", StringComparison.Ordinal));
        Assert.Contains(dns.Records, item => item.Id == "provider-mx");
        Assert.Contains(dns.Records, item => item.Id == "provider-dmarc" && item.Content == "v=DMARC1; p=reject");
        Assert.Contains(dns.Records, item => item.Id == "lms-dkim" && item.Content == publicDkim);
        Assert.All(result.DnsChecks.Where(item => item.Purpose is "DKIM" or "DMARC"), item => Assert.False(item.Changed));
    }

    [Fact]
    public async Task Removal_PreservesMxProviderDkimDmarcAndConcurrentSpfChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = MailRelayConfiguration.CreateDefault(now) with
        {
            Enabled = true,
            PublicIpAddress = "203.0.113.10",
            RelayHostname = "smtp.example.test"
        };
        var domain = new MailRelayDomain(
            Guid.NewGuid(), configuration.Id, "zone", "example.test", true, "lms2026", "secret://dkim", now, now,
            null, null, null, null, null, "lms-dkim", "shared-spf", "provider-dmarc",
            MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass, MailRelayDnsStatus.Pass,
            MailRelayDmarcPolicy.Reject, null, now, now);
        var records = new List<CloudflareDnsRecord>
        {
            Record("mx", "example.test", "MX", "example-test.mail.protection.outlook.com"),
            Record("provider-dkim", "selector1._domainkey.example.test", "CNAME", "selector1-example._domainkey.tenant.onmicrosoft.com"),
            Record("lms-a", "smtp.example.test", "A", "203.0.113.99"),
            Record("shared-spf", "example.test", "TXT", "v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com exists:new.provider.test -all"),
            Record("lms-dkim", "lms2026._domainkey.example.test", "TXT", "v=DKIM1; p=lms-key"),
            Record("provider-dmarc", "_dmarc.example.test", "TXT", "v=DMARC1; p=reject")
        };
        var tracked = new[]
        {
            new MailRelayDnsRecord(
                Guid.NewGuid(), domain.Id, "shared-spf", "TXT", "example.test", "SPF", false, true,
                "v=spf1 include:spf.protection.outlook.com -all",
                "v=spf1 ip4:203.0.113.10 include:spf.protection.outlook.com -all",
                MailRelayDnsChangeType.ModifiedShared, now, now),
            new MailRelayDnsRecord(
                Guid.NewGuid(), domain.Id, "lms-dkim", "TXT", "lms2026._domainkey.example.test", "DKIM", true, false,
                null, "v=DKIM1; p=lms-key", MailRelayDnsChangeType.Created, now, now),
            new MailRelayDnsRecord(
                Guid.NewGuid(), domain.Id, "lms-a", "A", "smtp.example.test", "Relay hostname", true, false,
                null, "203.0.113.10", MailRelayDnsChangeType.Created, now, now),
            new MailRelayDnsRecord(
                Guid.NewGuid(), domain.Id, "provider-dmarc", "TXT", "_dmarc.example.test", "DMARC", false, false,
                "v=DMARC1; p=reject", "v=DMARC1; p=reject", MailRelayDnsChangeType.ObservedExisting, now, now)
        };
        var store = new StubMailRelayStore(configuration, [domain], tracked);
        var dns = new StubDnsService(records);
        var service = new MailRelayProvisioningService(
            new StubPreflightService(BuildPreflight(now)),
            new StubExposureStore(new CloudflareSettings(
                AiLocalMachine.ManagedHostId, "account", "Account", "zone", "example.test", "secret://cloudflare", now, now)),
            dns,
            new StubSecretStore(),
            store,
            new RejectingPackageManagementService(),
            new RejectingServiceManagementService(),
            new AbsentContainerCommandRunner());

        var result = await service.RemoveAsync(new MailRelayRemovalRequest(false, false, true, false, true));

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, warning => warning.Contains("smtp.example.test", StringComparison.Ordinal));
        Assert.False(Assert.IsType<MailRelayConfiguration>(store.SavedConfiguration).Enabled);
        Assert.Contains(dns.Records, item => item.Type == "MX");
        Assert.Contains(dns.Records, item => item.Id == "provider-dkim");
        Assert.Contains(dns.Records, item => item.Id == "lms-a" && item.Content == "203.0.113.99");
        Assert.Contains(dns.Records, item => item.Id == "provider-dmarc" && item.Content == "v=DMARC1; p=reject");
        Assert.DoesNotContain(dns.Records, item => item.Id == "lms-dkim");
        Assert.Contains(dns.Records, item =>
            item.Id == "shared-spf" &&
            item.Content == "v=spf1 include:spf.protection.outlook.com exists:new.provider.test -all");
    }

    private static MailRelayProvisioningService CreateService(
        IReadOnlyList<CloudflareDnsRecord> records,
        StubMailRelayStore? store = null,
        MailRelayPreflightResult? preflight = null,
        IMailRelayOutboundService? outbound = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new MailRelayProvisioningService(
            new StubPreflightService(preflight ?? BuildPreflight(now)),
            new StubExposureStore(new CloudflareSettings(
                AiLocalMachine.ManagedHostId, "account", "Account", "zone", "example.test", "secret://cloudflare", now, now)),
            new StubDnsService(records),
            new StubSecretStore(),
            store ?? new StubMailRelayStore(),
            new RejectingPackageManagementService(),
            new RejectingServiceManagementService(),
            new RejectingCommandRunner(), outbound);
    }

    private static MailRelaySetupRequest Request() =>
        new("zone", "smtp.example.test", "example.test", "lms2026", "Flow Production", "flow-prod", true, false);

    private static CloudflareDnsRecord Record(string id, string name, string type, string content, string comment = "") =>
        new(id, "zone", name, type, content, false, 1, comment, null);

    private static MailRelayPreflightResult BuildPreflight(
        DateTimeOffset now,
        MailRelayPreflightCheck? docker = null,
        string zoneId = "zone",
        string zoneName = "example.test") =>
        new(zoneId, zoneName, [new MailRelayCloudflareZoneOption(zoneId, zoneName, "active", false, true)],
            $"smtp.{zoneName}", "203.0.113.10", string.Empty,
            [
                Check(MailRelayPreflightCheckKeys.EdgeGateway, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.CloudflareZone, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.DnsList, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.DnsEdit, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.PublicIpv4, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.OutboundSmtp, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.ReverseDns, MailRelayPreflightCheckState.Warning),
                docker ?? Check(MailRelayPreflightCheckKeys.Docker, MailRelayPreflightCheckState.Pass),
                Check(MailRelayPreflightCheckKeys.Tailscale, MailRelayPreflightCheckState.Pass)
            ], true, now);

    private static MailRelayPreflightCheck Check(string key, MailRelayPreflightCheckState state) =>
        new(key, key, state, state.ToString(), string.Empty);

    private sealed class StubPreflightService(MailRelayPreflightResult result) : IMailRelayPreflightService
    {
        public Task<MailRelayPublicIpv4DetectionResult> DetectPublicIpv4Async(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailRelayPublicIpv4DetectionResult(true, result.PublicIpAddress, "Detected."));

        public Task<MailRelayPreflightResult> InspectAsync(bool verifyDnsEdit, string? cloudflareZoneId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class StubDnsService(IReadOnlyList<CloudflareDnsRecord> records) : ICloudflareDnsService
    {
        public List<CloudflareDnsRecord> Records { get; } = records.ToList();
        public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(string apiToken, string zoneId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>(Records.ToArray());
        public Task<CloudflareDnsRecord> CreateRecordAsync(string apiToken, string zoneId, CloudflareDnsRecord record, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudflareDnsRecord> UpdateRecordAsync(string apiToken, string zoneId, CloudflareDnsRecord record, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(item => item.Id == record.Id);
            Records.Add(record);
            return Task.FromResult(record);
        }
        public Task DeleteRecordAsync(string apiToken, string zoneId, string recordId, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(item => item.Id == recordId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubExposureStore(CloudflareSettings settings) : ICloudflareExposureStore
    {
        public Task<CloudflareSettings?> GetSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default) => Task.FromResult<CloudflareSettings?>(settings);
        public Task SaveSettingsAsync(CloudflareSettings value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSettingsAsync(Guid managedHostId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExposedServiceConfig>> ListConfigsAsync(Guid managedHostId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExposedServiceConfig?> GetConfigAsync(Guid configId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExposedServiceConfig?> GetConfigByHostnameAsync(Guid managedHostId, string hostname, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveConfigAsync(ExposedServiceConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteConfigAsync(Guid configId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubSecretStore(string? dkimPrivateKey = null) : ISecretStore
    {
        public Task<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(secretReference.Equals("secret://dkim", StringComparison.Ordinal) ? dkimPrivateKey : "token");
        public Task<SecretReferenceMetadata?> GetMetadataAsync(string secretReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> StoreSecretAsync(string secretValue, string purpose, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubMailRelayStore : IMailRelayStore
    {
        private readonly List<MailRelayDnsRecord> dnsRecords;

        public StubMailRelayStore(
            MailRelayConfiguration? configuration = null,
            IEnumerable<MailRelayDomain>? domains = null,
            IEnumerable<MailRelayDnsRecord>? dnsRecords = null)
        {
            SavedConfiguration = configuration;
            SavedDomains = domains?.ToList() ?? [];
            this.dnsRecords = dnsRecords?.ToList() ?? [];
        }

        public MailRelayConfiguration? SavedConfiguration { get; private set; }
        public List<MailRelayDomain> SavedDomains { get; }

        public Task<MailRelayConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default) => Task.FromResult(SavedConfiguration);
        public Task SaveConfigurationAsync(MailRelayConfiguration configuration, CancellationToken cancellationToken = default)
        {
            SavedConfiguration = configuration;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<MailRelayDomain>> ListDomainsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayDomain>>(SavedDomains.ToArray());
        public Task SaveDomainAsync(MailRelayDomain domain, CancellationToken cancellationToken = default)
        {
            SavedDomains.RemoveAll(item => item.Id == domain.Id);
            SavedDomains.Add(domain);
            return Task.CompletedTask;
        }
        public Task DeleteDomainAsync(Guid domainId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayClient>> ListClientsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailRelayClient>>([]);
        public Task SaveClientAsync(MailRelayClient client, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailRelayDnsRecord>> ListDnsRecordsAsync(Guid domainId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailRelayDnsRecord>>(dnsRecords.Where(item => item.MailRelayDomainId == domainId).ToArray());
        public Task SaveDnsRecordAsync(MailRelayDnsRecord record, CancellationToken cancellationToken = default)
        {
            dnsRecords.RemoveAll(item => item.Id == record.Id);
            dnsRecords.Add(record);
            return Task.CompletedTask;
        }
        public Task DeleteDnsRecordAsync(Guid recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DnsLookupMissCommandRunner : ILinuxCommandRunner
    {
        public Task<LinuxCommandResult> RunAsync(LinuxCommandRequest request, bool dryRun, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxCommandResult(
                request.Description,
                1,
                string.Empty,
                "DNS propagation pending",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                dryRun));
    }

    private sealed class RejectingCommandRunner : ILinuxCommandRunner
    {
        public Task<LinuxCommandResult> RunAsync(LinuxCommandRequest request, bool dryRun, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preview must not execute provisioning commands.");
    }

    private sealed class AbsentContainerCommandRunner : ILinuxCommandRunner
    {
        public Task<LinuxCommandResult> RunAsync(LinuxCommandRequest request, bool dryRun, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxCommandResult(
                request.Description,
                1,
                string.Empty,
                "No such container",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                dryRun));
    }

    private sealed class RejectingPackageManagementService : IPackageManagementService
    {
        public Task<IReadOnlyList<PackageState>> InspectAsync(IReadOnlyList<string> packageNames, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preview must not inspect packages through the provisioning service.");

        public Task<IReadOnlyList<OperationLogEntry>> ApplyActionsAsync(IReadOnlyList<PackageAction> actions, bool dryRun, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preview must not install packages.");
    }

    private sealed class FailingPackageManagementService : IPackageManagementService
    {
        public Task<IReadOnlyList<PackageState>> InspectAsync(IReadOnlyList<string> packageNames, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OperationLogEntry>> ApplyActionsAsync(IReadOnlyList<PackageAction> actions, bool dryRun, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationLogEntry>>(
            [
                new OperationLogEntry(
                    DateTimeOffset.UtcNow,
                    OperationLogLevel.Error,
                    "Install Docker",
                    "apt-get install -y docker.io",
                    1,
                    string.Empty,
                    "simulated package failure")
            ]);
    }

    private sealed class RejectingServiceManagementService : IServiceManagementService
    {
        public Task<IReadOnlyList<ServiceState>> InspectAsync(IReadOnlyList<string> serviceNames, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preview must not inspect services through the provisioning service.");

        public Task<IReadOnlyList<OperationLogEntry>> ApplyActionsAsync(IReadOnlyList<ServiceAction> actions, bool dryRun, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Preview must not change services.");
    }

    private sealed class InlineProgress(Action<MailRelaySetupProgressUpdate> report) : IProgress<MailRelaySetupProgressUpdate>
    {
        public void Report(MailRelaySetupProgressUpdate value) => report(value);
    }
}
