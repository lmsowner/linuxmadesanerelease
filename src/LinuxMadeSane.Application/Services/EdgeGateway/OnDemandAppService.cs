// Copyright (c) Linux Made Sane.
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
    IOnDemandAppFavouriteStore favourites,
    IEdgeGatewayStore routes,
    IEdgeGatewayService gateway,
    EdgeGatewayOptions edgeGatewayOptions,
    OnDemandAppsOptions options,
    TimeProvider timeProvider)
{
    private const string RouteNotePrefix = "LMS On-Demand App lease=";
    private static readonly SemaphoreSlim RouteMutationLock = new(1, 1);

    public OnDemandAppsOptions Options => options;

    public async Task<OnDemandAppsAvailability> GetAvailabilityAsync(
        string publicHost,
        bool isHttps,
        CancellationToken cancellationToken = default)
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

        var dashboard = await gateway.GetDashboardAsync(cancellationToken);
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

    public Task<IReadOnlySet<string>> GetFavouritesAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        favourites.GetAsync(userId, cancellationToken);

    public Task SetFavouriteAsync(
        string userId,
        string serviceKey,
        bool isFavourite,
        CancellationToken cancellationToken = default) =>
        favourites.SetAsync(userId, serviceKey, isFavourite, cancellationToken);

    public async Task<OnDemandAppLaunch> OpenAsync(
        string serviceKey,
        Guid leaseId,
        string userEmail,
        string publicHost,
        bool isHttps,
        CancellationToken cancellationToken = default)
    {
        if (leaseId == Guid.Empty)
        {
            throw new InvalidOperationException("A valid browser lease is required.");
        }

        var normalizedEmail = NormalizeRequired(userEmail, "An authenticated LMS account is required.");
        var availability = await GetAvailabilityAsync(publicHost, isHttps, cancellationToken);
        if (!availability.IsAvailable)
        {
            throw new InvalidOperationException(availability.Message);
        }

        var normalizedServiceKey = NormalizeRequired(serviceKey, "Select a discovered app first.").ToLowerInvariant();
        var endpoint = (await discovery.GetCachedAsync(cancellationToken)).FirstOrDefault(item =>
            LocalHttpServiceDiscoveryRanking.StableKey(item).Equals(normalizedServiceKey, StringComparison.OrdinalIgnoreCase) &&
            !LocalHttpServiceDiscoveryRanking.IsHiddenFromPicker(item) &&
            item.Exposure != DiscoveryExposure.UnsafeToExpose);
        if (endpoint is null)
        {
            throw new InvalidOperationException("That app is no longer present in the LMS discovery cache. Run discovery again.");
        }

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
                StripForwardedFor = true,
                UsePublicHostHeader = false,
                SkipUpstreamTlsVerification = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
                Notes = BuildRouteNote(leaseId)
            };

            var routeId = await gateway.SaveRouteAsync(editor, cancellationToken);
            var provisioned = await gateway.ProvisionCloudflareRouteAsync(routeId, replaceExistingDnsRecord: false, cancellationToken);
            if (!provisioned.Success)
            {
                await gateway.DeleteRouteAsync(routeId, cancellationToken);
                _ = await gateway.ApplyCaddyConfigurationAsync(cancellationToken);
                throw new InvalidOperationException($"The temporary app address could not be published: {provisioned.Summary}");
            }

            var route = await routes.GetRouteAsync(routeId, cancellationToken) ??
                        throw new InvalidOperationException("The temporary app route was not saved.");
            return BuildLaunch(route, leaseId);
        }
        finally
        {
            RouteMutationLock.Release();
        }
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

    private static OnDemandAppsAvailability Unavailable(string message) => new(false, message);
}
