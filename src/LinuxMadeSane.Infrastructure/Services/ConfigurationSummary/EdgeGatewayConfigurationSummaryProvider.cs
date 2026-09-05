// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class EdgeGatewayConfigurationSummaryProvider(
    IEdgeGatewaySettingsStore edgeGatewaySettingsStore,
    IEdgeGatewayStore edgeGatewayStore,
    ICloudflareExposureStore cloudflareExposureStore) : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Edge Gateway";

    public int SortOrder => 20;

    public string NavigationUrl => "/edge-gateway";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var gatewaySettings = await edgeGatewaySettingsStore.GetAsync(cancellationToken);
        var routes = await edgeGatewayStore.ListRoutesAsync(cancellationToken);
        var cloudflare = await cloudflareExposureStore.GetSettingsAsync(AiLocalMachine.ManagedHostId, cancellationToken);
        var activeRoutes = routes.Where(route => route.Enabled).ToArray();
        var cloudflareConfigured = cloudflare is not null &&
                                   !string.IsNullOrWhiteSpace(cloudflare.ZoneId) &&
                                   !string.IsNullOrWhiteSpace(cloudflare.ApiTokenSecretReference);
        var configured = cloudflareConfigured || activeRoutes.Length > 0 || !string.IsNullOrWhiteSpace(gatewaySettings.TunnelInstanceId);
        if (!configured)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var warnings = new List<string>();
        if (!cloudflareConfigured)
        {
            warnings.Add("Cloudflare is not fully configured for this local Edge Gateway.");
        }

        var primaryHostname = activeRoutes
            .Select(route => route.Hostname)
            .FirstOrDefault(hostname => !string.IsNullOrWhiteSpace(hostname));
        if (string.IsNullOrWhiteSpace(primaryHostname) && !string.IsNullOrWhiteSpace(cloudflare?.ZoneName))
        {
            primaryHostname = $"{gatewaySettings.GatewaySubdomain}.{cloudflare.ZoneName}";
        }

        var authenticationModes = activeRoutes
            .Select(route => SummaryValueFormatter.Words(route.AuthMode.ToString()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LmsConfigurationSummary(
            ModuleName,
            warnings.Count == 0 ? LmsConfigurationSummaryStatus.Configured : LmsConfigurationSummaryStatus.Warning,
            "Cloudflare-backed public access",
            [
                new("Primary hostname", primaryHostname ?? "Not configured"),
                new("Cloudflare zone", cloudflare?.ZoneName ?? "Not configured"),
                new("Cloudflare token", cloudflareConfigured ? "Configured" : "Not configured"),
                new("Published services", activeRoutes.Length.ToString()),
                new("HTTPS", activeRoutes.Length > 0 ? "Enabled" : "Not configured"),
                new("Authentication", authenticationModes.Length == 0 ? "Not configured" : string.Join(" / ", authenticationModes))
            ],
            warnings,
            "/edge-gateway",
            SortOrder);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Cloudflare-backed public access", [], [], "/edge-gateway", SortOrder);
}
