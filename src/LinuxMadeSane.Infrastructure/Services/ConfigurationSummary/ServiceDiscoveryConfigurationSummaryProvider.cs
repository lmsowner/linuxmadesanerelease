// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.SystemInfo;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;

namespace LinuxMadeSane.Infrastructure.Services.ConfigurationSummary;

public sealed class ServiceDiscoveryConfigurationSummaryProvider(ILocalHttpServiceDiscoveryService discoveryService)
    : ILmsConfigurationSummaryProvider
{
    public string ModuleName => "Service Discovery";

    public int SortOrder => 90;

    public string NavigationUrl => "/edge-gateway";

    public async Task<LmsConfigurationSummary> GetConfigurationSummaryAsync(CancellationToken cancellationToken = default)
    {
        var services = await discoveryService.GetCachedAsync(cancellationToken);
        if (services.Count == 0)
        {
            return Empty(LmsConfigurationSummaryStatus.NotConfigured);
        }

        var scopes = services.Select(service => service.Scope)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mostRecent = services
            .Where(service => service.DiscoveredAtUtc.HasValue)
            .Select(service => service.DiscoveredAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();

        var items = new List<LmsConfigurationSummaryItem>
        {
            new("Cached services", services.Count.ToString()),
            new("Discovery scopes", scopes.Length == 0 ? "Not recorded" : string.Join(" / ", scopes))
        };
        if (mostRecent != default)
        {
            items.Add(new LmsConfigurationSummaryItem("Last discovery", mostRecent.ToLocalTime().ToString("dd MMM yyyy HH:mm")));
        }

        return new LmsConfigurationSummary(
            ModuleName,
            LmsConfigurationSummaryStatus.Configured,
            "Cached local HTTP/S discovery",
            items,
            [],
            "/edge-gateway",
            SortOrder);
    }

    private LmsConfigurationSummary Empty(LmsConfigurationSummaryStatus status) =>
        new(ModuleName, status, "Cached local HTTP/S discovery", [], [], "/edge-gateway", SortOrder);
}
