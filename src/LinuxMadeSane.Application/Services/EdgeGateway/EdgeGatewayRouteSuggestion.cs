// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Services.EdgeGateway;

public sealed record EdgeGatewayRouteSuggestion(string DisplayName, string Subdomain, string TargetHost);

public static class EdgeGatewayRouteSuggestionFactory
{
    public static EdgeGatewayRouteSuggestion FromDiscoveredService(LocalHttpServiceEndpoint service)
    {
        ArgumentNullException.ThrowIfNull(service);

        var discoveredHost = service.Host.Trim().TrimEnd('.');
        var hasDnsHost = !string.IsNullOrWhiteSpace(discoveredHost) &&
                         !IPAddress.TryParse(discoveredHost, out _) &&
                         !discoveredHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var readableName = hasDnsHost
            ? discoveredHost
            : !LocalHttpServiceDiscoveryRanking.IsUnknownLabel(service.DisplayName)
                ? service.DisplayName!.Trim()
                : LocalHttpServiceDiscoveryRanking.PickerLabel(service);

        var nameSource = hasDnsHost ? discoveredHost.Split('.')[0] : readableName;
        var subdomain = Regex.Replace(nameSource.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            subdomain = $"service-{service.Port}";
        }

        var ipAddress = service.IpAddress?.Trim();
        var targetHost = IPAddress.TryParse(ipAddress, out _) ? ipAddress! : discoveredHost;
        return new EdgeGatewayRouteSuggestion(readableName, subdomain, targetHost);
    }
}
