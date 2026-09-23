// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed partial class EdgeGatewayRouteRegistrationService(
    IEdgeGatewayService gateway) : IEdgeGatewayRouteRegistrationService
{
    public async Task<EdgeGatewayClientEndpoint> RegisterClientRouteAsync(
        EdgeGatewayClientRouteRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var routing = ResolveRouting(registration.RoutingStrategy, registration.BasePathSupport);
        var originHost = EdgeGatewayRouteValidator.NormalizeHostname(registration.OriginHostname);
        var domain = EdgeGatewayRouteValidator.NormalizeDomainName(registration.DomainName);
        var host = routing == HomeLabClientRoutingStrategy.Subdomain
            ? EdgeGatewayRouteValidator.NormalizeHostname($"{BuildServiceLabel(registration.ServiceId)}.{originHost}")
            : originHost;
        var path = routing == HomeLabClientRoutingStrategy.Subpath
            ? NormalizePreferredPath(registration.ServiceId, registration.PreferredPath)
            : string.Empty;

        var siblings = (await gateway.ListRoutesAsync(cancellationToken))
            .Where(route => route.Enabled &&
                            route.Id != registration.ExistingRouteId &&
                            route.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (siblings.Any(route => route.AuthMode != registration.AuthMode))
        {
            throw new InvalidOperationException($"Every route on shared origin {host} must use the same Edge Gateway authentication policy.");
        }

        var routeId = await gateway.SaveRouteAsync(
            new EdgeGatewayRouteEditor
            {
                Id = registration.ExistingRouteId,
                Enabled = true,
                DisplayName = registration.DisplayName,
                Hostname = host,
                DomainName = domain,
                TargetScheme = EdgeGatewayTargetScheme.Http,
                TargetHost = registration.TargetHost,
                TargetPort = registration.TargetPort,
                TargetPathPrefix = path,
                StripPathPrefix = registration.StripPathPrefix,
                ForwardPathPrefix = registration.ForwardPathPrefix,
                AuthMode = registration.AuthMode,
                UsePublicHostHeader = registration.UsePublicHostHeader,
                StripForwardedFor = false,
                SkipUpstreamTlsVerification = true,
                Notes = $"LMS managed route; owner={registration.OwnerType}:{registration.OwnerId}; service={registration.ServiceId}"
            },
            cancellationToken);

        var published = await gateway.ProvisionCloudflareRouteAsync(routeId, false, cancellationToken);
        if (!published.Success)
        {
            throw new InvalidOperationException(published.Summary);
        }

        return new EdgeGatewayClientEndpoint(
            routeId,
            $"https://{host}{path}",
            "https",
            host,
            path,
            routing);
    }

    public Task UnregisterClientRouteAsync(Guid routeId, CancellationToken cancellationToken = default) =>
        gateway.DeletePublishedRouteAsync(routeId, cancellationToken);

    private static HomeLabClientRoutingStrategy ResolveRouting(
        HomeLabClientRoutingStrategy requested,
        HomeLabBasePathSupportMode support) =>
        requested switch
        {
            HomeLabClientRoutingStrategy.Auto when support == HomeLabBasePathSupportMode.None => HomeLabClientRoutingStrategy.Subdomain,
            HomeLabClientRoutingStrategy.Auto => HomeLabClientRoutingStrategy.Subpath,
            HomeLabClientRoutingStrategy.Subpath when support == HomeLabBasePathSupportMode.None =>
                throw new InvalidOperationException("The service cannot be registered on a subpath because its provider metadata declares no base-path support."),
            _ => requested
        };

    private static string NormalizePreferredPath(string serviceId, string preferredPath)
    {
        var path = string.IsNullOrWhiteSpace(preferredPath) ? $"/{BuildServiceLabel(serviceId)}" : preferredPath.Trim();
        if (path == "/")
        {
            return string.Empty;
        }
        return EdgeGatewayRouteValidator.NormalizePathPrefix(path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}");
    }

    private static string BuildServiceLabel(string value)
    {
        var label = UnsafeHostnameCharacter().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        if (label.Length == 0)
        {
            throw new InvalidOperationException("The service ID cannot be converted to a public hostname label.");
        }
        return label.Length <= 63 ? label : label[..63].TrimEnd('-');
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeHostnameCharacter();
}
