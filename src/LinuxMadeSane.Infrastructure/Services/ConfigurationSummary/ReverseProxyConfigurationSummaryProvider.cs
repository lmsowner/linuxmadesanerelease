// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class ReverseProxyConfigurationSummaryProvider(ICaddyIntegrationDataService dataService)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Reverse Proxy";

    public int SortOrder => 80;

    public string NavigationUrl => "/integrations";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var routes = await dataService.ListRoutesAsync(cancellationToken);
        if (routes.Count == 0)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var hostnameRoutes = routes.Where(route => route.Kind == CaddyProxyRouteKind.HostnameReverseProxy).ToArray();
        return new LmsConfigurationSummary(
            ModuleName,
            LmsConfigurationSummaryStatus.Configured,
            "Caddy routes managed by LMS",
            [
                new("Routes", routes.Count.ToString()),
                new("Hostname routes", hostnameRoutes.Length.ToString()),
                new("HTTPS routes", hostnameRoutes.Count(route => route.EnableTls).ToString()),
                new("Port forwards", routes.Count(route => route.Kind == CaddyProxyRouteKind.PortForward).ToString())
            ],
            [],
            "/integrations",
            SortOrder);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Caddy routes managed by LMS", [], [], "/integrations", SortOrder);
}
