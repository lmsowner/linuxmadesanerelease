// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed class OnDemandAppService(
    ILocalHttpServiceDiscoveryService discovery,
    ILocalHttpServiceProxyCompatibilityService proxyCompatibility,
    IOnDemandAppFavouriteStore favourites,
    IEdgeGatewayStore routes,
    IEdgeGatewayService gateway,
    IPublicDnsPropagationService publicDns,
    EdgeGatewayOptions edgeGatewayOptions,
    OnDemandAppsOptions options,
    TimeProvider timeProvider)
{
    private const string RouteNotePrefix = "LMS On-Demand App lease=";
    private static readonly SemaphoreSlim RouteMutationLock = new(1, 1);

    public OnDemandAppsOptions Options => options;

    public Task<OnDemandAppsAvailability> GetAvailabilityAsync(
        string publicHost,
        bool isHttps,
        CancellationToken cancellationToken = default) =>
        GetAvailabilityCoreAsync(publicHost, isHttps, dashboard: null, cancellationToken);

    public Task<OnDemandAppsAvailability> GetAvailabilityAsync(
        string publicHost,
        bool isHttps,
        EdgeGatewayDashboardViewModel dashboard,
        CancellationToken cancellationToken = default) =>
        GetAvailabilityCoreAsync(publicHost, isHttps, dashboard, cancellationToken);

    private async Task<OnDemandAppsAvailability> GetAvailabilityCoreAsync(
        string publicHost,
        bool isHttps,
        EdgeGatewayDashboardViewModel? dashboard,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizePublicHost(publicHost);
        if (!isHttps)
        {
            return Unavailable("Open LMS through its published HTTPS address to use On-Demand Apps.");
        }

        if (!IsFqdn(normalizedHost))
        {
            return Unavailable("Open LMS through the FQDN published from Edge Gateway Setup to use On-Demand Apps.");
        }

        var publishedRoute = (await routes.ListRoutesAsync(cancellationToken)).FirstOrDefault(route =>
            route.Enabled &&
            route.Hostname.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase) &&
            EdgeGatewayServerPublishingService.IsManagedServerRoute(route, edgeGatewayOptions));
        if (publishedRoute is null)
        {
            return Unavailable("This LMS address was not published from Edge Gateway Setup.");
        }

        dashboard ??= await gateway.GetDashboardAsync(cancellationToken);
        var domain = dashboard.Cloudflare.Domains.FirstOrDefault(item =>
            item.DomainName.Equals(publishedRoute.DomainName, StringComparison.OrdinalIgnoreCase));
        if (domain is not { Paused: false, RelayConfigured: true, RelayUsesCloudflareTunnel: true, RelayOwnedByThisLms: true })
        {
            return Unavailable("The Cloudflare wildcard relay owned by this LMS server is not ready.");
        }

        return new OnDemandAppsAvailability(
            true,
            $"Apps open through temporary authenticated addresses in {domain.DomainName}.",
            publishedRoute.Hostname,
            domain.GatewayDomainName,
            domain.DomainName);
    }

    public Task<IReadOnlyList<OnDemandAppFavourite>> GetFavouritesAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        favourites.ListAsync(userId, cancellationToken);

    public Task SaveFavouriteAsync(
        string userId,
        LocalHttpServiceEndpoint endpoint,
        OnDemandAppProxyPreferences proxyPreferences,
        CancellationToken cancellationToken = default) =>
        favourites.SaveAsync(
            userId,
            new OnDemandAppFavourite(
                LocalHttpServiceDiscoveryRanking.StableKey(endpoint),
                endpoint,
                proxyPreferences,
                timeProvider.GetUtcNow()),
            cancellationToken);

    public Task<LocalHttpServiceProxyProfile> TestConnectionAsync(
        LocalHttpServiceEndpoint endpoint,
        string publicHostname,
        OnDemandAppProxyPreferences preferences,
        CancellationToken cancellationToken = default) =>
        proxyCompatibility.TestAsync(ApplyTargetAddressPreference(endpoint, preferences.TargetAddress),
            publicHostname, preferences, cancellationToken);

    public Task RemoveFavouriteAsync(
        string userId,
        string serviceKey,
        CancellationToken cancellationToken = default) =>
        favourites.RemoveAsync(userId, serviceKey, cancellationToken);

    public async Task<OnDemandAppLaunch> OpenAsync(
        string serviceKey,
        Guid leaseId,
        string userId,
        string userEmail,
        string publicHost,
        bool isHttps,
        CancellationToken cancellationToken = default,
        IProgress<OnDemandAppLaunchProgress>? progress = null)
    {
        if (leaseId == Guid.Empty)
        {
            throw new InvalidOperationException("A valid browser lease is required.");
        }

        var normalizedUserId = NormalizeRequired(userId, "An authenticated LMS account is required.");
        var normalizedEmail = NormalizeRequired(userEmail, "An authenticated LMS account is required.");
        Report(progress, "Checking the published LMS address and Edge Gateway relay.");
        var availability = await GetAvailabilityAsync(publicHost, isHttps, cancellationToken);
        if (!availability.IsAvailable)
        {
            throw new InvalidOperationException(availability.Message);
        }

        var normalizedServiceKey = NormalizeRequired(serviceKey, "Select a discovered app first.").ToLowerInvariant();
        Report(progress, "Loading the discovered app and its saved connection settings.");
        var favourite = (await favourites.ListAsync(normalizedUserId, cancellationToken)).FirstOrDefault(item =>
            item.ServiceKey.Equals(normalizedServiceKey, StringComparison.OrdinalIgnoreCase));
        var endpoint = (await discovery.GetCachedAsync(cancellationToken)).FirstOrDefault(item =>
            LocalHttpServiceDiscoveryRanking.StableKey(item).Equals(normalizedServiceKey, StringComparison.OrdinalIgnoreCase) &&
            !LocalHttpServiceDiscoveryRanking.IsHiddenFromPicker(item) &&
            item.Exposure != DiscoveryExposure.UnsafeToExpose) ?? favourite?.Endpoint;
        if (endpoint is null)
        {
            throw new InvalidOperationException("That app has no saved endpoint. Run discovery again to refresh the favourite.");
        }

        if (LocalHttpServiceDiscoveryRanking.IsHiddenFromPicker(endpoint) || endpoint.Exposure == DiscoveryExposure.UnsafeToExpose)
        {
            throw new InvalidOperationException("That saved app is not currently safe to expose. Run discovery again.");
        }

        var proxyPreferences = favourite?.ProxyPreferences ?? new OnDemandAppProxyPreferences();
        endpoint = ApplyTargetAddressPreference(endpoint, proxyPreferences.TargetAddress);

        Report(progress, "Waiting for the Edge Gateway route manager.");
        await RouteMutationLock.WaitAsync(cancellationToken);
        try
        {
            var existingRoutes = await routes.ListRoutesAsync(cancellationToken);
            var existingLease = existingRoutes.FirstOrDefault(route => IsLeaseRoute(route, leaseId));
            if (existingLease is not null)
            {
                if (!ContainsUser(existingLease.AllowedUsers, normalizedEmail))
                {
                    throw new InvalidOperationException("That On-Demand App lease belongs to another LMS account.");
                }

                if (IsDirectZoneHostname(existingLease.Hostname, availability.DomainName))
                {
                    Report(progress, "Refreshing the existing Cloudflare, tunnel and Caddy route.");
                    var existingProvision = await gateway.ProvisionCloudflareRouteAsync(
                        existingLease.Id,
                        replaceExistingDnsRecord: false,
                        cancellationToken);
                    if (!existingProvision.Success)
                    {
                        throw new InvalidOperationException($"The temporary app address could not be verified: {existingProvision.Summary}");
                    }

                    Report(progress, $"Waiting for {existingLease.Hostname} to become reachable.");
                    await EnsurePublicAddressReadyAsync(existingLease.Id, existingLease.Hostname, cancellationToken);
                    Report(progress, "The temporary authenticated address is ready.");
                    return BuildLaunch(existingLease, leaseId);
                }

                // Releases before v2026.09.18.14.08 placed the browser-facing hostname below
                // the relay namespace, outside the zone's normal one-label TLS certificate.
                await gateway.DeleteRouteAsync(existingLease.Id, cancellationToken);
                var removedLegacyRoute = await gateway.ApplyCaddyConfigurationAsync(cancellationToken);
                if (!removedLegacyRoute.Success)
                {
                    await routes.SaveRouteAsync(existingLease, cancellationToken);
                    throw new InvalidOperationException($"The obsolete temporary route could not be replaced: {removedLegacyRoute.Summary}");
                }
            }

            var activeCount = existingRoutes.Count(IsOnDemandRoute);
            if (activeCount >= Math.Clamp(options.MaximumActiveRoutes, 1, 256))
            {
                throw new InvalidOperationException("The On-Demand App route limit has been reached. Close an open app or wait for stale routes to be cleaned up.");
            }

            var hostname = $"ondemand-{leaseId:N}"[..21] + $".{availability.DomainName}";
            Report(progress, "Testing HTTP/S, address and proxy header combinations.");
            var proxyProfile = await proxyCompatibility.SelectAsync(endpoint, hostname, proxyPreferences, cancellationToken);
            endpoint = proxyProfile.Endpoint;
            if (proxyPreferences.ConnectAsLms)
            {
                Report(progress, $"Connecting from {proxyPreferences.SourceInterface} ({proxyPreferences.SourceAddress}); external client-IP headers are removed from the application hop.");
            }
            Report(progress, $"Selected {endpoint.Scheme.ToUpperInvariant()} to {endpoint.Host}:{endpoint.Port}.");
            var editor = new EdgeGatewayRouteEditor
            {
                Enabled = true,
                DisplayName = $"On-Demand: {LocalHttpServiceDiscoveryRanking.PickerLabel(endpoint)}",
                Hostname = hostname,
                DomainName = availability.DomainName,
                TargetScheme = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? EdgeGatewayTargetScheme.Https
                    : EdgeGatewayTargetScheme.Http,
                TargetHost = endpoint.Host,
                TargetPort = endpoint.Port,
                AuthMode = EdgeGatewayAuthMode.RequireMfa,
                AllowedUsers = normalizedEmail,
                StripForwardedFor = proxyProfile.StripForwardedFor,
                UpstreamSourceAddress = proxyPreferences.ConnectAsLms ? proxyPreferences.SourceAddress : string.Empty,
                UsePublicHostHeader = proxyProfile.UsePublicHostHeader,
                SkipUpstreamTlsVerification = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                Notes = BuildRouteNote(leaseId)
            };

            Report(progress, $"Creating the authenticated address {hostname}.");
            var routeId = await gateway.SaveRouteAsync(editor, cancellationToken);
            Report(progress, "Updating Cloudflare DNS, tunnel ingress and Caddy.");
            var provisioned = await gateway.ProvisionCloudflareRouteAsync(routeId, replaceExistingDnsRecord: false, cancellationToken);
            if (!provisioned.Success)
            {
                await gateway.DeleteRouteAsync(routeId, cancellationToken);
                _ = await gateway.ApplyCaddyConfigurationAsync(cancellationToken);
                throw new InvalidOperationException($"The temporary app address could not be published: {provisioned.Summary}");
            }

            Report(progress, $"Waiting for {hostname} to become reachable.");
            await EnsurePublicAddressReadyAsync(routeId, hostname, cancellationToken);

            var route = await routes.GetRouteAsync(routeId, cancellationToken) ??
                        throw new InvalidOperationException("The temporary app route was not saved.");
            Report(progress, "The temporary authenticated address is ready.");
            return BuildLaunch(route, leaseId);
        }
        finally
        {
            RouteMutationLock.Release();
        }
    }

    private static LocalHttpServiceEndpoint ApplyTargetAddressPreference(
        LocalHttpServiceEndpoint endpoint,
        OnDemandAppTargetAddressPreference preference)
    {
        var targetHost = preference == OnDemandAppTargetAddressPreference.DiscoveredIp &&
                         !string.IsNullOrWhiteSpace(endpoint.IpAddress)
            ? endpoint.IpAddress.Trim()
            : endpoint.Host.Trim();
        if (targetHost.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        return endpoint with
        {
            Host = targetHost,
            Url = new UriBuilder(endpoint.Scheme, targetHost, endpoint.Port, "/").Uri.AbsoluteUri
        };
    }

    public async Task<bool> TouchAsync(
        Guid leaseId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var route = (await routes.ListRoutesAsync(cancellationToken)).FirstOrDefault(item => IsLeaseRoute(item, leaseId));
        if (route is null || !ContainsUser(route.AllowedUsers, userEmail))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (now - route.UpdatedAt >= TimeSpan.FromSeconds(20))
        {
            await routes.SaveRouteAsync(route with { UpdatedAt = now }, cancellationToken);
        }

        return true;
    }

    public async Task<bool> TouchByHostnameAsync(
        string hostname,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var normalizedHostname = NormalizePublicHost(hostname);
        var route = (await routes.ListRoutesAsync(cancellationToken)).FirstOrDefault(item =>
            IsOnDemandRoute(item) &&
            item.Hostname.Equals(normalizedHostname, StringComparison.OrdinalIgnoreCase));
        if (route is null || !ContainsUser(route.AllowedUsers, userEmail))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (now - route.UpdatedAt >= TimeSpan.FromSeconds(20))
        {
            await routes.SaveRouteAsync(route with { UpdatedAt = now }, cancellationToken);
        }

        return true;
    }

    public async Task<bool> ReleaseAsync(
        Guid leaseId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        await RouteMutationLock.WaitAsync(cancellationToken);
        try
        {
            var route = (await routes.ListRoutesAsync(cancellationToken)).FirstOrDefault(item => IsLeaseRoute(item, leaseId));
            if (route is null || !ContainsUser(route.AllowedUsers, userEmail))
            {
                return false;
            }

            await RemoveRoutesAndApplyAsync([route], cancellationToken);
            return true;
        }
        finally
        {
            RouteMutationLock.Release();
        }
    }

    public async Task<int> CleanupStaleAsync(CancellationToken cancellationToken = default)
    {
        await RouteMutationLock.WaitAsync(cancellationToken);
        try
        {
            var cutoff = timeProvider.GetUtcNow().Subtract(options.IdleTimeout);
            var stale = (await routes.ListRoutesAsync(cancellationToken))
                .Where(route => IsOnDemandRoute(route) && route.UpdatedAt < cutoff)
                .ToArray();
            if (stale.Length == 0)
            {
                return 0;
            }

            await RemoveRoutesAndApplyAsync(stale, cancellationToken);
            return stale.Length;
        }
        finally
        {
            RouteMutationLock.Release();
        }
    }

    public static bool IsOnDemandRoute(EdgeGatewayRoute route) =>
        route.Notes.StartsWith(RouteNotePrefix, StringComparison.Ordinal);

    private async Task RemoveRoutesAndApplyAsync(
        IReadOnlyList<EdgeGatewayRoute> routesToRemove,
        CancellationToken cancellationToken)
    {
        var legacyRoutes = routesToRemove
            .Where(route => !IsDirectZoneHostname(route.Hostname, route.DomainName))
            .ToArray();
        foreach (var route in legacyRoutes)
        {
            await gateway.DeleteRouteAsync(route.Id, cancellationToken);
        }

        if (legacyRoutes.Length > 0)
        {
            var applied = await gateway.ApplyCaddyConfigurationAsync(cancellationToken);
            if (!applied.Success)
            {
                foreach (var route in legacyRoutes)
                {
                    await routes.SaveRouteAsync(route, cancellationToken);
                }

                throw new InvalidOperationException($"On-Demand App cleanup could not update Caddy: {applied.Summary}");
            }
        }

        foreach (var route in routesToRemove.Where(route => IsDirectZoneHostname(route.Hostname, route.DomainName)))
        {
            await gateway.DeletePublishedRouteAsync(route.Id, cancellationToken);
        }
    }

    private OnDemandAppLaunch BuildLaunch(EdgeGatewayRoute route, Guid leaseId) =>
        new(leaseId, route.Hostname, $"https://{route.Hostname}/", route.UpdatedAt.Add(options.IdleTimeout));

    private async Task EnsurePublicAddressReadyAsync(
        Guid routeId,
        string hostname,
        CancellationToken cancellationToken)
    {
        if (await publicDns.WaitUntilResolvableAsync(hostname, options.DnsPropagationTimeout, cancellationToken))
        {
            return;
        }

        await gateway.DeletePublishedRouteAsync(routeId, cancellationToken);
        throw new InvalidOperationException(
            $"The temporary app address was created, but public DNS did not make {hostname} resolvable within {options.DnsPropagationTimeout.TotalSeconds:0} seconds. Try opening the app again.");
    }

    private static bool IsLeaseRoute(EdgeGatewayRoute route, Guid leaseId) =>
        route.Notes.Equals(BuildRouteNote(leaseId), StringComparison.Ordinal);

    private static string BuildRouteNote(Guid leaseId) => $"{RouteNotePrefix}{leaseId:N}";

    private static bool ContainsUser(string allowedUsers, string userEmail) =>
        EdgeGatewayRouteValidator.SplitList(allowedUsers)
            .Contains(userEmail.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string NormalizePublicHost(string host) => (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static bool IsDirectZoneHostname(string hostname, string domainName)
    {
        var normalizedHostname = NormalizePublicHost(hostname);
        var normalizedDomain = NormalizePublicHost(domainName);
        if (!normalizedHostname.EndsWith($".{normalizedDomain}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = normalizedHostname[..^(normalizedDomain.Length + 1)];
        return relative.Length > 0 && !relative.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsFqdn(string host) =>
        !string.IsNullOrWhiteSpace(host) &&
        host.Contains('.', StringComparison.Ordinal) &&
        !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !IPAddress.TryParse(host, out _) &&
        Uri.CheckHostName(host) == UriHostNameType.Dns;

    private static string NormalizeRequired(string value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value.Trim();

    private static void Report(IProgress<OnDemandAppLaunchProgress>? progress, string message) =>
        progress?.Report(new OnDemandAppLaunchProgress(message));

    private static OnDemandAppsAvailability Unavailable(string message) => new(false, message);
}
