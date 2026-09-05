// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;
using LinuxMadeSane.Application.Contracts.MailRelay;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class MailRelayPreflightService(
    ICloudflareExposureStore cloudflareStore,
    ICloudflareZoneService cloudflareZoneService,
    ICloudflareDnsService cloudflareDnsService,
    ISecretStore secretStore,
    ILinuxCommandRunner commandRunner,
    IHttpClientFactory httpClientFactory) : IMailRelayPreflightService
{
    private static readonly string[] PublicMxTargets =
    [
        "gmail-smtp-in.l.google.com",
        "outlook-com.olc.protection.outlook.com",
        "mta5.am0.yahoodns.net"
    ];

    public async Task<MailRelayPreflightResult> InspectAsync(
        bool verifyDnsEdit,
        string? cloudflareZoneId = null,
        CancellationToken cancellationToken = default)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        var settings = await cloudflareStore.GetSettingsAsync(AiLocalMachine.ManagedHostId, cancellationToken);
        if (settings is null ||
            string.IsNullOrWhiteSpace(settings.ZoneId) ||
            string.IsNullOrWhiteSpace(settings.ZoneName) ||
            string.IsNullOrWhiteSpace(settings.ApiTokenSecretReference))
        {
            return MissingEdgeGatewayResult(verifyDnsEdit, checkedAtUtc);
        }

        var zoneName = settings.ZoneName.Trim().TrimEnd('.');
        IReadOnlyList<MailRelayCloudflareZoneOption> availableZones = [];
        var checks = new List<MailRelayPreflightCheck>
        {
            Check(MailRelayPreflightCheckKeys.EdgeGateway, "Edge Gateway", MailRelayPreflightCheckState.Pass,
                "CONFIGURED", $"Cloudflare zone {zoneName} is managed by Edge Gateway.")
        };

        string? apiToken;
        try
        {
            apiToken = await secretStore.ResolveSecretAsync(settings.ApiTokenSecretReference, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            apiToken = null;
        }

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, "Cloudflare connection",
                MailRelayPreflightCheckState.Failed, "FAILED", "The Edge Gateway Cloudflare token could not be resolved."));
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareZone, "Configured zone",
                MailRelayPreflightCheckState.NotAvailable, zoneName, "Cloudflare could not be authenticated."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsList, "DNS access",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "Cloudflare could not be authenticated."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "Cloudflare could not be authenticated."));
            AddHostChecksNotAvailable(checks, "Complete the Edge Gateway Cloudflare connection first.");
            return BuildResult(settings.ZoneId, zoneName, availableZones, string.Empty, string.Empty, checks, verifyDnsEdit, checkedAtUtc);
        }

        CloudflareZone? selectedZone = null;
        try
        {
            var zones = await cloudflareZoneService.ListZonesAsync(apiToken, cancellationToken);
            availableZones = zones
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new MailRelayCloudflareZoneOption(
                    item.Id,
                    item.Name.Trim().TrimEnd('.'),
                    item.Status,
                    item.Paused,
                    item.Id.Equals(settings.ZoneId, StringComparison.Ordinal)))
                .ToArray();

            selectedZone = zones.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(cloudflareZoneId) &&
                    item.Id.Equals(cloudflareZoneId.Trim(), StringComparison.Ordinal))
                ?? zones.FirstOrDefault(item => item.Id.Equals(settings.ZoneId, StringComparison.Ordinal))
                ?? zones.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

            if (selectedZone is null)
            {
                throw new InvalidOperationException("No Cloudflare zones were returned for the saved token.");
            }

            zoneName = selectedZone.Name.Trim().TrimEnd('.');
            checks[0] = Check(MailRelayPreflightCheckKeys.EdgeGateway, "Edge Gateway", MailRelayPreflightCheckState.Pass,
                "CONFIGURED", $"The saved token provides {availableZones.Count} Cloudflare zone(s); {zoneName} is selected.");
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, "Cloudflare connection",
                MailRelayPreflightCheckState.Pass, "PASS", "The saved Edge Gateway token authenticated successfully."));
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareZone, "Configured zone",
                MailRelayPreflightCheckState.Pass, zoneName, $"The selected zone is accessible. {availableZones.Count} zone(s) are available."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, "Cloudflare connection",
                MailRelayPreflightCheckState.Failed, "FAILED", "Cloudflare rejected the saved token or configured zone."));
            checks.Add(Check(MailRelayPreflightCheckKeys.CloudflareZone, "Configured zone",
                MailRelayPreflightCheckState.Failed, zoneName, "The configured zone could not be accessed."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsList, "DNS access",
                MailRelayPreflightCheckState.Failed, "FAILED", "DNS records could not be listed."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "Resolve Cloudflare access before testing DNS Edit."));
            AddHostChecksNotAvailable(checks, "Complete the Edge Gateway Cloudflare connection first.");
            return BuildResult(
                selectedZone?.Id ?? settings.ZoneId,
                zoneName,
                availableZones,
                string.Empty,
                string.Empty,
                checks,
                verifyDnsEdit,
                checkedAtUtc);
        }

        try
        {
            await cloudflareDnsService.ListRecordsAsync(apiToken, selectedZone.Id, cancellationToken);
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsList, "DNS access",
                MailRelayPreflightCheckState.Pass, "PASS", "DNS records can be listed through the existing Cloudflare integration."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsList, "DNS access",
                MailRelayPreflightCheckState.Failed, "FAILED", $"DNS records for {zoneName} could not be listed."));
            checks.Add(Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "Select a zone with DNS access or update the Edge Gateway token scope."));
            AddHostChecksNotAvailable(checks, "Cloudflare DNS access must pass before host suitability is tested.");
            return BuildResult(selectedZone.Id, zoneName, availableZones, string.Empty, string.Empty, checks, verifyDnsEdit, checkedAtUtc);
        }

        checks.Add(verifyDnsEdit
            ? await VerifyDnsEditAsync(apiToken, selectedZone.Id, zoneName, cancellationToken)
            : Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management", MailRelayPreflightCheckState.NotRun,
                "NOT TESTED", "Run preflight to verify Zone → DNS → Edit with a temporary record."));

        if (!verifyDnsEdit)
        {
            AddHostChecksNotRun(checks);
            return BuildResult(selectedZone.Id, zoneName, availableZones, string.Empty, string.Empty, checks, false, checkedAtUtc);
        }

        var publicIpTask = DetectPublicIpv4Async(cancellationToken);
        var smtpTask = TestOutboundSmtpAsync(cancellationToken);
        var dockerTask = InspectDockerAsync(cancellationToken);
        var tailscaleTask = InspectTailscaleAsync(cancellationToken);

        await Task.WhenAll(publicIpTask, smtpTask, dockerTask, tailscaleTask);

        var publicIpResult = await publicIpTask;
        checks.Add(publicIpResult.Check);
        checks.Add(await InspectReverseDnsAsync(publicIpResult.Address, cancellationToken));
        checks.Add(await smtpTask);
        checks.Add(await dockerTask);
        checks.Add(await tailscaleTask);

        var reverseHostname = checks
            .Single(item => item.Key == MailRelayPreflightCheckKeys.ReverseDns)
            .State == MailRelayPreflightCheckState.Pass
                ? checks.Single(item => item.Key == MailRelayPreflightCheckKeys.ReverseDns).Value
                : string.Empty;

        return BuildResult(
            selectedZone.Id,
            zoneName,
            availableZones,
            publicIpResult.Address?.ToString() ?? string.Empty,
            reverseHostname,
            checks,
            true,
            checkedAtUtc);
    }

    internal async Task<MailRelayPreflightCheck> VerifyDnsEditAsync(
        string apiToken,
        string zoneId,
        string zoneName,
        CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var recordName = $"_lms-mail-relay-preflight-{nonce[..12]}.{zoneName}";
        CloudflareDnsRecord? created = null;

        try
        {
            created = await cloudflareDnsService.CreateRecordAsync(
                apiToken,
                zoneId,
                new CloudflareDnsRecord(
                    string.Empty,
                    zoneId,
                    recordName,
                    "TXT",
                    $"lms-mail-relay-preflight={nonce}",
                    false,
                    1,
                    "Managed by Linux Made Sane Mail Relay (permission preflight)",
                    null),
                cancellationToken);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CloudflareApiException exception)
        {
            var permissionFailure = exception.StatusCode is 401 or 403;
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Failed,
                permissionFailure ? "PERMISSION REQUIRED" : "TEST FAILED",
                permissionFailure
                    ? "Cloudflare rejected DNS record creation for this zone. Confirm the token includes Zone → DNS → Edit and that this zone is in its resource scope."
                    : $"Cloudflare could not create the permission-test record: {exception.Message}");
        }
        catch (Exception exception)
        {
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Failed, "TEST FAILED",
                $"The DNS write test could not complete: {exception.Message}");
        }

        try
        {
            await cloudflareDnsService.DeleteRecordAsync(apiToken, zoneId, created.Id, cancellationToken);
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Pass, "PASS", "Zone → DNS → Edit was verified and the temporary record was removed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCleanupDnsProbeAsync(apiToken, zoneId, created);
            throw;
        }
        catch
        {
            var cleanedUp = await TryCleanupDnsProbeAsync(apiToken, zoneId, created);
            return Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management",
                MailRelayPreflightCheckState.Pass,
                "PASS",
                cleanedUp
                    ? "DNS Edit was verified by creating a temporary record. Its first removal attempt failed, but the cleanup retry succeeded."
                    : $"DNS Edit was verified by creating {recordName}. Cloudflare did not accept either cleanup attempt; remove that temporary TXT record manually.");
        }
    }

    private async Task<bool> TryCleanupDnsProbeAsync(
        string apiToken,
        string zoneId,
        CloudflareDnsRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Id))
        {
            return true;
        }

        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await cloudflareDnsService.DeleteRecordAsync(apiToken, zoneId, record.Id, cleanupTimeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(MailRelayPreflightCheck Check, IPAddress? Address)> DetectPublicIpv4Async(
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(MailRelayPreflightService));
            var trace = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken);
            var value = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1]
                .Trim();

            if (IPAddress.TryParse(value, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IsPrivateIpv4(address))
            {
                return (Check(MailRelayPreflightCheckKeys.PublicIpv4, "Public IPv4",
                    MailRelayPreflightCheckState.Pass, address.ToString(), "The public egress IPv4 address was detected."), address);
            }

            return (Check(MailRelayPreflightCheckKeys.PublicIpv4, "Public IPv4",
                MailRelayPreflightCheckState.Failed, "NOT AVAILABLE", "A public IPv4 address could not be detected."), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (Check(MailRelayPreflightCheckKeys.PublicIpv4, "Public IPv4",
                MailRelayPreflightCheckState.Failed, "NOT AVAILABLE", "The public IPv4 check could not reach its detection endpoint."), null);
        }
    }

    private static async Task<MailRelayPreflightCheck> InspectReverseDnsAsync(
        IPAddress? address,
        CancellationToken cancellationToken)
    {
        if (address is null)
        {
            return Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", "A public IPv4 address is required before PTR can be checked.");
        }

        try
        {
            var reverse = await Dns.GetHostEntryAsync(address).WaitAsync(cancellationToken);
            var hostname = reverse.HostName.Trim().TrimEnd('.');
            var forward = await Dns.GetHostAddressesAsync(hostname, cancellationToken);
            var matches = forward.Any(candidate => candidate.Equals(address));

            return matches
                ? Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                    MailRelayPreflightCheckState.Pass, hostname, $"{address} and {hostname} resolve back to each other.")
                : Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                    MailRelayPreflightCheckState.Warning, "MISMATCH", $"PTR resolves to {hostname}, but its A record does not resolve to {address}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS",
                MailRelayPreflightCheckState.Warning, "NOT CONFIGURED", $"Set the provider-managed PTR for {address} to the relay hostname.");
        }
    }

    private static async Task<MailRelayPreflightCheck> TestOutboundSmtpAsync(CancellationToken cancellationToken)
    {
        var attempts = PublicMxTargets.Select(target => CanConnectAsync(target, 25, cancellationToken)).ToArray();
        var results = await Task.WhenAll(attempts);
        var reachable = results.Count(result => result);

        return reachable > 0
            ? Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound SMTP :25",
                MailRelayPreflightCheckState.Pass, "PASS", $"Connected to {reachable} of {PublicMxTargets.Length} independent public MX targets on TCP/25.")
            : Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound SMTP :25",
                MailRelayPreflightCheckState.Failed, "BLOCKED", "No tested public MX target accepted a TCP/25 connection. The hosting provider may block outbound SMTP.");
    }

    private static async Task<bool> CanConnectAsync(
        string hostname,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(hostname, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    internal async Task<MailRelayPreflightCheck> InspectDockerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "docker",
                    ["--version"],
                    false,
                    TimeSpan.FromSeconds(10),
                    "Check whether Docker is installed")
                {
                    IsOptionalExternalTool = true
                },
                false,
                cancellationToken);

            if (version.ExitCode != 0)
            {
                var apt = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "apt-get",
                        ["--version"],
                        false,
                        TimeSpan.FromSeconds(10),
                        "Check whether Docker can be installed")
                    {
                        IsOptionalExternalTool = true
                    },
                    false,
                    cancellationToken);

                return apt.ExitCode == 0
                    ? Check(MailRelayPreflightCheckKeys.Docker, "Docker",
                        MailRelayPreflightCheckState.Warning, "INSTALL REQUIRED",
                        "Docker is not installed. Mail Relay setup can install the Ubuntu docker.io package and start it after you approve the reviewed changes.")
                    : Check(MailRelayPreflightCheckKeys.Docker, "Docker",
                        MailRelayPreflightCheckState.Failed, "NOT INSTALLED",
                        "Docker is not installed and this host does not provide the apt package manager LMS uses to install it.");
            }

            var direct = await RunDockerInfoAsync(requiresSudo: false, cancellationToken);
            if (direct.ExitCode == 0)
            {
                return DockerPass(direct.StandardOutput, usedPrivilegeBoundary: false);
            }

            var privileged = await RunDockerInfoAsync(requiresSudo: true, cancellationToken);
            if (privileged.ExitCode == 0)
            {
                return DockerPass(privileged.StandardOutput, usedPrivilegeBoundary: true);
            }

            var service = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "systemctl",
                    ["is-active", "docker.service"],
                    false,
                    TimeSpan.FromSeconds(10),
                    "Check whether Docker is running"),
                false,
                cancellationToken);

            return service.ExitCode != 0
                ? Check(MailRelayPreflightCheckKeys.Docker, "Docker",
                    MailRelayPreflightCheckState.Warning, "START REQUIRED",
                    "Docker is installed but its service is not running. Mail Relay setup can enable and start it after you approve the reviewed changes.")
                : Check(MailRelayPreflightCheckKeys.Docker, "Docker",
                    MailRelayPreflightCheckState.Failed, "INACCESSIBLE",
                    "Docker is running, but LMS could not access it through either its normal account or the configured privileged command runner.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Check(MailRelayPreflightCheckKeys.Docker, "Docker",
                MailRelayPreflightCheckState.Failed, "NOT AVAILABLE", "Docker could not be inspected.");
        }
    }

    private Task<LinuxCommandResult> RunDockerInfoAsync(
        bool requiresSudo,
        CancellationToken cancellationToken) =>
        commandRunner.RunAsync(
            new LinuxCommandRequest(
                "docker",
                ["info", "--format", "{{.ServerVersion}}"],
                requiresSudo,
                TimeSpan.FromSeconds(10),
                "Check the Docker runtime")
            {
                IsOptionalExternalTool = true
            },
            false,
            cancellationToken);

    private static MailRelayPreflightCheck DockerPass(string version, bool usedPrivilegeBoundary)
    {
        var normalizedVersion = version.Trim();
        return Check(
            MailRelayPreflightCheckKeys.Docker,
            "Docker",
            MailRelayPreflightCheckState.Pass,
            "PASS",
            $"Docker{(string.IsNullOrWhiteSpace(normalizedVersion) ? string.Empty : $" {normalizedVersion}")} is available and running{(usedPrivilegeBoundary ? " through the LMS privileged runner" : string.Empty)}.");
    }

    private async Task<MailRelayPreflightCheck> InspectTailscaleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reader = new TailscalePeerStatusReader(commandRunner);
            var snapshot = await reader.ReadAsync(cancellationToken);
            return snapshot.Availability == TailscalePeerStatusAvailability.Ready
                ? Check(MailRelayPreflightCheckKeys.Tailscale, "Tailscale",
                    MailRelayPreflightCheckState.Pass, "PASS", "An active Tailscale interface is available for private SMTP submission.")
                : Check(MailRelayPreflightCheckKeys.Tailscale, "Tailscale",
                    MailRelayPreflightCheckState.Warning, "NOT AVAILABLE", snapshot.FailureMessage ?? "Tailscale is not ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Check(MailRelayPreflightCheckKeys.Tailscale, "Tailscale",
                MailRelayPreflightCheckState.Warning, "NOT AVAILABLE", "Tailscale could not be inspected; trusted LAN submission remains an option.");
        }
    }

    private static MailRelayPreflightResult MissingEdgeGatewayResult(bool dnsEditWasTested, DateTimeOffset checkedAtUtc)
    {
        var checks = new List<MailRelayPreflightCheck>
        {
            Check(MailRelayPreflightCheckKeys.EdgeGateway, "Edge Gateway", MailRelayPreflightCheckState.Failed,
                "NOT CONFIGURED", "Configure Cloudflare in Edge Gateway before setting up Mail Relay."),
            Check(MailRelayPreflightCheckKeys.CloudflareAuthentication, "Cloudflare connection", MailRelayPreflightCheckState.NotAvailable,
                "NOT CONFIGURED", "The Cloudflare token stays owned by Edge Gateway."),
            Check(MailRelayPreflightCheckKeys.CloudflareZone, "Configured zone", MailRelayPreflightCheckState.NotAvailable,
                "NOT AVAILABLE", "Select a Cloudflare zone in Edge Gateway."),
            Check(MailRelayPreflightCheckKeys.DnsList, "DNS access", MailRelayPreflightCheckState.NotAvailable,
                "NOT AVAILABLE", "Configure and test Cloudflare DNS in Edge Gateway."),
            Check(MailRelayPreflightCheckKeys.DnsEdit, "DNS management", MailRelayPreflightCheckState.NotAvailable,
                "NOT AVAILABLE", "Configure and test Cloudflare DNS in Edge Gateway.")
        };
        AddHostChecksNotAvailable(checks, "Mail Relay preflight is locked until Edge Gateway is configured.");
        return BuildResult(string.Empty, string.Empty, [], string.Empty, string.Empty, checks, dnsEditWasTested, checkedAtUtc);
    }

    private static void AddHostChecksNotRun(ICollection<MailRelayPreflightCheck> checks)
    {
        checks.Add(Check(MailRelayPreflightCheckKeys.PublicIpv4, "Public IPv4", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to detect the public IPv4 address."));
        checks.Add(Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to validate PTR and forward DNS."));
        checks.Add(Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound SMTP :25", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to connect to multiple public MX servers."));
        checks.Add(Check(MailRelayPreflightCheckKeys.Docker, "Docker", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to inspect the container runtime."));
        checks.Add(Check(MailRelayPreflightCheckKeys.Tailscale, "Tailscale", MailRelayPreflightCheckState.NotRun,
            "NOT TESTED", "Run preflight to inspect private submission availability."));
    }

    private static void AddHostChecksNotAvailable(ICollection<MailRelayPreflightCheck> checks, string detail)
    {
        checks.Add(Check(MailRelayPreflightCheckKeys.PublicIpv4, "Public IPv4", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
        checks.Add(Check(MailRelayPreflightCheckKeys.ReverseDns, "Reverse DNS", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
        checks.Add(Check(MailRelayPreflightCheckKeys.OutboundSmtp, "Outbound SMTP :25", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
        checks.Add(Check(MailRelayPreflightCheckKeys.Docker, "Docker", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
        checks.Add(Check(MailRelayPreflightCheckKeys.Tailscale, "Tailscale", MailRelayPreflightCheckState.NotAvailable, "NOT AVAILABLE", detail));
    }

    private static MailRelayPreflightResult BuildResult(
        string zoneId,
        string zoneName,
        IReadOnlyList<MailRelayCloudflareZoneOption> availableZones,
        string publicIpAddress,
        string reverseDnsHostname,
        IReadOnlyList<MailRelayPreflightCheck> checks,
        bool dnsEditWasTested,
        DateTimeOffset checkedAtUtc) =>
        new(
            zoneId,
            zoneName,
            availableZones,
            string.IsNullOrWhiteSpace(zoneName) ? string.Empty : $"smtp.{zoneName}",
            publicIpAddress,
            reverseDnsHostname,
            checks,
            dnsEditWasTested,
            checkedAtUtc);

    private static MailRelayPreflightCheck Check(
        string key,
        string label,
        MailRelayPreflightCheckState state,
        string value,
        string detail) =>
        new(key, label, state, value, detail);

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}
