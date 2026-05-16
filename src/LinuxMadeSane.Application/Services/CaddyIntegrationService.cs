// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Caddy;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Caddy;

namespace LinuxMadeSane.Application.Services;

public sealed class CaddyIntegrationService(ICaddyIntegrationDataService dataService) : ICaddyIntegrationService
{
    public async Task<CaddyIntegrationDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await dataService.GetSnapshotAsync(cancellationToken);
        return new CaddyIntegrationDashboardViewModel(
            snapshot.IsInstalled,
            snapshot.InstalledVersion,
            snapshot.IsServiceActive,
            snapshot.IsServiceEnabled,
            snapshot.IsManagedImportConfigured,
            snapshot.IsConfigurationValid,
            snapshot.ValidationSummary,
            snapshot.MainConfigPath,
            snapshot.ManagedConfigPath,
            snapshot.Routes
                .OrderBy(route => route.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(route => route.Hostname, StringComparer.OrdinalIgnoreCase)
                .Select(MapListItem)
                .ToArray(),
            []);
    }

    public async Task<CaddyProxyRouteEditor> GetEditorAsync(Guid? routeId, CancellationToken cancellationToken = default)
    {
        if (!routeId.HasValue)
        {
            return new CaddyProxyRouteEditor();
        }

        var route = await dataService.GetRouteAsync(routeId.Value, cancellationToken);
        return route is null
            ? new CaddyProxyRouteEditor()
            : new CaddyProxyRouteEditor
            {
                Id = route.Id,
                Name = route.Name,
                Hostname = route.Hostname,
                UpstreamUrl = route.UpstreamUrl,
                Description = route.Description,
                EnableTls = route.EnableTls
            };
    }

    public async Task<Guid> SaveRouteAsync(CaddyProxyRouteEditor editor, CancellationToken cancellationToken = default)
    {
        var routeId = editor.Id ?? Guid.NewGuid();
        var existing = editor.Id.HasValue ? await dataService.GetRouteAsync(editor.Id.Value, cancellationToken) : null;
        var now = DateTimeOffset.UtcNow;
        var hostname = NormalizeHostname(editor.Hostname);
        var upstreamUrl = NormalizeUpstreamUrl(editor.UpstreamUrl);

        var route = new CaddyProxyRouteDefinition(
            routeId,
            editor.Name.Trim(),
            hostname,
            upstreamUrl,
            editor.Description.Trim(),
            editor.EnableTls,
            existing?.CreatedAtUtc ?? now,
            now);

        await dataService.SaveRouteAsync(route, cancellationToken);
        return routeId;
    }

    public Task DeleteRouteAsync(Guid routeId, CancellationToken cancellationToken = default) =>
        dataService.DeleteRouteAsync(routeId, cancellationToken);

    public async Task<CaddyOperationResultViewModel> InstallAsync(CancellationToken cancellationToken = default) =>
        MapResult(await dataService.InstallAsync(cancellationToken));

    public async Task<CaddyOperationResultViewModel> ReloadAsync(CancellationToken cancellationToken = default) =>
        MapResult(await dataService.ReloadAsync(cancellationToken));

    public async Task<CaddyOperationResultViewModel> RestartAsync(CancellationToken cancellationToken = default) =>
        MapResult(await dataService.RestartAsync(cancellationToken));

    private static CaddyOperationResultViewModel MapResult(CaddyOperationResult result) =>
        new(result.Success, result.Summary, result.Logs);

    private static CaddyProxyRouteListItem MapListItem(CaddyProxyRouteDefinition route) =>
        new(
            route.Id,
            route.Name,
            route.Hostname,
            route.UpstreamUrl,
            route.Description,
            route.EnableTls,
            route.EnableTls ? route.Hostname : $"http://{route.Hostname}",
            BuildSnippet(route),
            route.UpdatedAtUtc);

    private static string BuildSnippet(CaddyProxyRouteDefinition route)
    {
        var address = route.EnableTls ? route.Hostname : $"http://{route.Hostname}";
        return $@"{address} {{
    encode zstd gzip
    reverse_proxy {route.UpstreamUrl}
}}";
    }

    private static string NormalizeHostname(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }
        else if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[8..];
        }

        normalized = normalized.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('/') ||
            normalized.Contains(' '))
        {
            throw new InvalidOperationException("Enter a clean public hostname such as app.example.com.");
        }

        return normalized;
    }

    private static string NormalizeUpstreamUrl(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Enter the upstream URL that Caddy should reverse proxy to.");
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("Enter a valid upstream URL such as http://127.0.0.1:3000.");
        }

        return uri.ToString().TrimEnd('/');
    }
}
