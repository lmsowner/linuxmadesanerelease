// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed class EdgeGatewayServerPublishingService(
    IEdgeGatewayService gateway,
    IEdgeGatewayStore routes,
    ISecurityUserStore users,
    ICloudflareExposureStore exposureStore,
    IExposedServiceManager exposures,
    ISecretStore secrets,
    ICloudflareDnsService dns,
    ICloudflareTunnelService tunnels,
    ITrustedNetworkAccessService networkAccess,
    EdgeGatewayOptions options,
    HttpClient? availabilityClient = null)
{
    private const string ManagedRouteNote = "LMS server published from Edge Gateway Setup.";
    private static readonly SemaphoreSlim PublishLock = new(1, 1);
    private static readonly HttpClient LocalHealthClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public static string BuildServerHostname(string machineName, string domainName)
    {
        var label = machineName.Trim().Split('.')[0].ToLowerInvariant();
        if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
            label.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new InvalidOperationException("The server hostname must be a valid DNS label before it can be published.");
        }

        return EdgeGatewayRouteValidator.NormalizeHostname($"{label}.{EdgeGatewayRouteValidator.NormalizeDomainName(domainName)}");
    }

    public async Task<EdgeGatewayCloudflareRouteSetupResult> PublishAsync(
        string domainName,
        CancellationToken cancellationToken = default)
    {
        await PublishLock.WaitAsync(cancellationToken);
        try
        {
            var accounts = await users.ListAsync(cancellationToken);
            if (accounts.Count == 0)
                throw new InvalidOperationException("Set up an LMS Account in LMS Security first, then publish this server.");
            if (!accounts.Any(user => user.IsEnabled))
                throw new InvalidOperationException("Enable an LMS Account in LMS Security before publishing this server.");

            var hostname = BuildServerHostname(Environment.MachineName, domainName);
            var dashboard = await gateway.GetDashboardAsync(cancellationToken);
            var zone = dashboard.Cloudflare.Domains.FirstOrDefault(item => item.DomainName.Equals(domainName, StringComparison.OrdinalIgnoreCase));
            if (zone is not { Paused: false, RelayConfigured: true, RelayUsesCloudflareTunnel: true, RelayOwnedByThisLms: true })
                throw new InvalidOperationException("Set up a relay owned by this LMS server for the selected zone first.");

            var existingRoutes = (await routes.ListRoutesAsync(cancellationToken))
                .Where(route => route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase)).ToArray();
            var existing = existingRoutes.SingleOrDefault(route => IsManagedServerRoute(route, options));
            if (existingRoutes.Any(route => route.Id != existing?.Id))
                throw new InvalidOperationException($"{hostname} is already used by an Edge Gateway service. No records were changed.");
            if (await exposureStore.GetConfigByHostnameAsync(AiLocalMachine.ManagedHostId, hostname, cancellationToken) is not null)
                throw new InvalidOperationException($"{hostname} is already used by a Cloudflare exposure. No records were changed.");

            var access = await networkAccess.EvaluateAsync(IPAddress.Loopback, hostname, cancellationToken);
            if (!access.IsAllowed)
                throw new InvalidOperationException("Allow the local gateway connection in LMS Security before publishing this server.");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var health = await (availabilityClient ?? LocalHealthClient).GetFromJsonAsync<ServerHealth>(
                    $"http://127.0.0.1:{options.LmsForwardAuthPort}/healthz", timeout.Token);
                if (health is not { Product: "linux-made-sane", Status: "ok" })
                    throw new InvalidOperationException("The local LMS server did not pass its availability check.");
            }

            var settings = await exposureStore.GetSettingsAsync(AiLocalMachine.ManagedHostId, cancellationToken);
            var token = string.IsNullOrWhiteSpace(settings?.ApiTokenSecretReference) ? null :
                await secrets.ResolveSecretAsync(settings.ApiTokenSecretReference, cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Save a valid Cloudflare token in Edge Gateway Setup first.");

            var relayHostname = $"{hostname[..^(zone.DomainName.Length + 1)]}.{zone.GatewayDomainName}";
            var records = await dns.ListRecordsAsync(token, zone.ZoneId, cancellationToken);
            if (records.Any(record => HostnameMatches(record.Name, hostname) &&
                !(existing is not null && record.Name.TrimEnd('.').Equals(hostname, StringComparison.OrdinalIgnoreCase) &&
                  record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) &&
                  record.Content.TrimEnd('.').Equals(relayHostname, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException($"{hostname} is already covered by a DNS record. No records were changed.");

            var validation = await exposures.ValidateTokenAsync(AiLocalMachine.ManagedHostId, null, cancellationToken);
            var accountIds = validation.Accounts.Select(account => account.Id).Append(zone.AccountId).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var accountId in accountIds)
            {
                foreach (var tunnel in (await tunnels.ListTunnelsAsync(token, accountId, cancellationToken)).Where(tunnel => !tunnel.IsDeleted))
                {
                    if (!tunnel.ConfigSource.Equals("cloudflare", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Cannot verify hostname availability in locally managed tunnel {tunnel.Name}. Check its configuration before using manual service setup.");
                    var configuration = await tunnels.GetConfigurationAsync(token, accountId, tunnel.Id, cancellationToken);
                    if (configuration.Routes.Any(route => HostnameMatches(route.Hostname, hostname) &&
                        !(existing is not null && tunnel.Id == zone.RelayTunnelId &&
                          route.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) &&
                          route.Service.TrimEnd('/').Equals(options.CaddyLocalServiceUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))))
                        throw new InvalidOperationException($"{hostname} is already used by Cloudflare tunnel {tunnel.Name}. No records were changed.");
                }
            }

            // Apply MFA locally before any public DNS or tunnel changes. Never replace another service's DNS.
            var editor = new EdgeGatewayRouteEditor
            {
                Id = existing?.Id,
                Enabled = true,
                DisplayName = $"LMS Server ({Environment.MachineName})",
                Hostname = hostname,
                DomainName = zone.DomainName,
                TargetHost = "127.0.0.1",
                TargetPort = options.LmsForwardAuthPort,
                TargetScheme = EdgeGatewayTargetScheme.Http,
                AuthMode = EdgeGatewayAuthMode.RequireMfa,
                AllowedUsers = existing?.AllowedUsers ?? string.Empty,
                AllowedGroups = existing?.AllowedGroups ?? string.Empty,
                AllowKnownIps = existing?.AllowKnownIps ?? string.Empty,
                AllowLanOnly = existing?.AllowLanOnly ?? false,
                StripForwardedFor = true,
                UsePublicHostHeader = true,
                SkipUpstreamTlsVerification = false,
                Notes = ManagedRouteNote
            };
            var routeId = await gateway.SaveRouteAsync(editor, cancellationToken);
            var applied = await gateway.ApplyCaddyConfigurationAsync(cancellationToken);
            if (!applied.Success)
                throw new InvalidOperationException($"MFA routing could not be applied. The server was not published: {applied.Summary}");
            return await gateway.ProvisionCloudflareRouteAsync(routeId, replaceExistingDnsRecord: false, cancellationToken);
        }
        finally
        {
            PublishLock.Release();
        }
    }

    public static bool IsManagedServerRoute(EdgeGatewayRoute route, EdgeGatewayOptions options) =>
        route.Notes == ManagedRouteNote && route.TargetHost == "127.0.0.1" &&
        route.TargetPort == options.LmsForwardAuthPort && route.TargetScheme == EdgeGatewayTargetScheme.Http &&
        string.IsNullOrEmpty(route.TargetPathPrefix);

    private static bool HostnameMatches(string pattern, string hostname)
    {
        pattern = pattern.Trim().TrimEnd('.');
        return pattern.Equals(hostname, StringComparison.OrdinalIgnoreCase) ||
               (pattern.StartsWith("*.", StringComparison.Ordinal) && hostname.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ServerHealth(string Status, string Product);
}
