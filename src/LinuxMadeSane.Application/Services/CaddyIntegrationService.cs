// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;
using System.Text;
using LinuxMadeSane.Application.Contracts.Caddy;
using LinuxMadeSane.Application.Contracts.EdgeGateway;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Caddy;

namespace LinuxMadeSane.Application.Services;

public sealed class CaddyIntegrationService(
    ICaddyIntegrationDataService dataService,
    EdgeGatewayOptions? edgeGatewayOptions = null) : ICaddyIntegrationService
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
                Kind = route.Kind,
                Hostname = route.Hostname,
                UpstreamUrl = route.UpstreamUrl,
                Description = route.Description,
                EnableTls = route.EnableTls,
                SourceIp = string.IsNullOrWhiteSpace(route.SourceIp) ? "127.0.0.1" : route.SourceIp,
                SourcePort = route.SourcePort > 0 ? route.SourcePort : 8080,
                DestinationIp = string.IsNullOrWhiteSpace(route.DestinationIp) ? "127.0.0.1" : route.DestinationIp,
                DestinationPort = route.DestinationPort > 0 ? route.DestinationPort : 80,
                DestinationScheme = route.DestinationScheme
            };
    }

    public async Task<Guid> SaveRouteAsync(CaddyProxyRouteEditor editor, CancellationToken cancellationToken = default)
    {
        var routeId = editor.Id ?? Guid.NewGuid();
        var existing = editor.Id.HasValue ? await dataService.GetRouteAsync(editor.Id.Value, cancellationToken) : null;
        var now = DateTimeOffset.UtcNow;
        var route = editor.Kind == CaddyProxyRouteKind.PortForward
            ? BuildPortForwardRoute(editor, routeId, existing?.CreatedAtUtc ?? now, now)
            : BuildHostnameRoute(editor, routeId, existing?.CreatedAtUtc ?? now, now);

        var blockingWarning = BuildBlockingWarning(route);
        if (!string.IsNullOrWhiteSpace(blockingWarning))
        {
            throw new InvalidOperationException(blockingWarning);
        }

        await dataService.SaveRouteAsync(route, cancellationToken);
        return routeId;
    }

    public Task DeleteRouteAsync(Guid routeId, CancellationToken cancellationToken = default) =>
        dataService.DeleteRouteAsync(routeId, cancellationToken);

    public async Task<CaddyOperationResultViewModel> CheckRouteAsync(Guid routeId, CancellationToken cancellationToken = default) =>
        MapResult(await dataService.CheckRouteAsync(routeId, cancellationToken));

    public async Task<CaddyOperationResultViewModel> InstallAsync(CancellationToken cancellationToken = default) =>
        MapResult(await dataService.InstallAsync(cancellationToken));

    public async Task<CaddyOperationResultViewModel> ReloadAsync(CancellationToken cancellationToken = default) =>
        MapResult(await dataService.ReloadAsync(cancellationToken));

    public async Task<CaddyOperationResultViewModel> RestartAsync(CancellationToken cancellationToken = default) =>
        MapResult(await dataService.RestartAsync(cancellationToken));

    private static CaddyOperationResultViewModel MapResult(CaddyOperationResult result) =>
        new(result.Success, result.Summary, result.Logs);

    private CaddyProxyRouteListItem MapListItem(CaddyProxyRouteDefinition route) =>
        new(
            route.Id,
            route.Kind,
            route.Kind == CaddyProxyRouteKind.PortForward ? "Port forward" : "Hostname route",
            route.Name,
            route.Hostname,
            route.UpstreamUrl,
            route.Description,
            route.EnableTls,
            BuildAddressLabel(route),
            BuildTargetLabel(route),
            BuildSnippet(route),
            BuildWarnings(route),
            route.UpdatedAtUtc);

    private static string BuildSnippet(CaddyProxyRouteDefinition route)
    {
        return route.Kind == CaddyProxyRouteKind.PortForward
            ? BuildPortForwardSnippet(route)
            : BuildHostnameSnippet(route);
    }

    private static string BuildHostnameSnippet(CaddyProxyRouteDefinition route)
    {
        var address = route.EnableTls ? route.Hostname : $"http://{route.Hostname}";
        return $@"{address} {{
    encode zstd gzip
    reverse_proxy {route.UpstreamUrl}
}}";
    }

    private static string BuildPortForwardSnippet(CaddyProxyRouteDefinition route)
    {
        var listenPort = Math.Clamp(route.SourcePort, 1, 65535);
        var sourceIp = CaddyBindAddressFormatter.NormalizeCsv(route.SourceIp);
        var targetUrl = BuildPortForwardTargetUrl(route);
        var builder = new StringBuilder();

        builder.AppendLine($"http://:{listenPort} {{");
        if (!CaddyBindAddressFormatter.IsAnyAddressList(sourceIp))
        {
            builder.AppendLine($"    bind {CaddyBindAddressFormatter.ToCaddyBindArguments(sourceIp)}");
        }

        builder.AppendLine("    encode zstd gzip");
        if (route.DestinationScheme == CaddyProxyTargetScheme.Https)
        {
            builder.AppendLine($"    reverse_proxy {targetUrl} {{");
            builder.AppendLine("        transport http {");
            builder.AppendLine("            tls_insecure_skip_verify");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
        }
        else
        {
            builder.AppendLine($"    reverse_proxy {targetUrl}");
        }

        builder.Append('}');
        return builder.ToString();
    }

    private CaddyProxyRouteDefinition BuildHostnameRoute(
        CaddyProxyRouteEditor editor,
        Guid routeId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        var hostname = NormalizeHostname(editor.Hostname);
        var upstreamUrl = NormalizeUpstreamUrl(editor.UpstreamUrl);
        return new CaddyProxyRouteDefinition(
            routeId,
            NormalizeName(editor.Name),
            hostname,
            upstreamUrl,
            editor.Description.Trim(),
            editor.EnableTls,
            createdAtUtc,
            updatedAtUtc);
    }

    private CaddyProxyRouteDefinition BuildPortForwardRoute(
        CaddyProxyRouteEditor editor,
        Guid routeId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        var sourceIp = CaddyBindAddressFormatter.NormalizeCsv(editor.SourceIp);
        var sourcePort = NormalizePort(editor.SourcePort, "source port");
        var destinationIp = NormalizeDestinationHost(editor.DestinationIp);
        var destinationPort = NormalizePort(editor.DestinationPort, "destination port");
        var targetUrl = BuildPortForwardTargetUrl(destinationIp, destinationPort, editor.DestinationScheme);

        return new CaddyProxyRouteDefinition(
            routeId,
            NormalizeName(editor.Name),
            CaddyBindAddressFormatter.FormatEndpointLabel(sourceIp, sourcePort),
            targetUrl,
            editor.Description.Trim(),
            false,
            createdAtUtc,
            updatedAtUtc,
            CaddyProxyRouteKind.PortForward,
            sourceIp,
            sourcePort,
            destinationIp,
            destinationPort,
            editor.DestinationScheme);
    }

    private string? BuildBlockingWarning(CaddyProxyRouteDefinition route)
    {
        if (route.Kind != CaddyProxyRouteKind.PortForward)
        {
            return null;
        }

        var edgeGatewayPort = ResolveEdgeGatewayCaddyPort();
        if (route.SourcePort == edgeGatewayPort)
        {
            return $"Source port {route.SourcePort} is reserved for the Edge Gateway Caddy listener. Pick another port so the gateway mapping is not broken.";
        }

        return null;
    }

    private IReadOnlyList<string> BuildWarnings(CaddyProxyRouteDefinition route)
    {
        var warnings = new List<string>();
        if (route.Kind != CaddyProxyRouteKind.PortForward)
        {
            return warnings;
        }

        var edgeGatewayPort = ResolveEdgeGatewayCaddyPort();
        if (route.SourcePort == edgeGatewayPort)
        {
            warnings.Add($"Uses Edge Gateway listener port {edgeGatewayPort}; this will conflict with Edge Gateway.");
        }

        if (route.SourcePort is 80 or 443)
        {
            warnings.Add("Uses a standard web port; check existing Caddy hostname routes before applying.");
        }

        if (CaddyBindAddressFormatter.IsAnyAddressList(route.SourceIp))
        {
            warnings.Add("Listens on all interfaces. Use a Tailnet or LAN IP when the forward should not be public.");
        }

        return warnings;
    }

    private int ResolveEdgeGatewayCaddyPort()
    {
        var value = edgeGatewayOptions?.CaddyLocalServiceUrl;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsDefaultPort)
            {
                return Math.Clamp(uri.Port, 1, 65535);
            }

            return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        }

        return 8443;
    }

    private static string BuildAddressLabel(CaddyProxyRouteDefinition route) =>
        route.Kind == CaddyProxyRouteKind.PortForward
            ? CaddyBindAddressFormatter.FormatEndpointLabel(route.SourceIp, route.SourcePort)
            : route.EnableTls ? route.Hostname : $"http://{route.Hostname}";

    private static string BuildTargetLabel(CaddyProxyRouteDefinition route) =>
        route.Kind == CaddyProxyRouteKind.PortForward
            ? FormatEndpointLabel(route.DestinationIp, route.DestinationPort)
            : route.UpstreamUrl;

    private static string BuildPortForwardTargetUrl(CaddyProxyRouteDefinition route) =>
        BuildPortForwardTargetUrl(route.DestinationIp, route.DestinationPort, route.DestinationScheme);

    private static string BuildPortForwardTargetUrl(
        string destinationHost,
        int destinationPort,
        CaddyProxyTargetScheme scheme)
    {
        var schemeText = scheme == CaddyProxyTargetScheme.Https ? "https" : "http";
        return $"{schemeText}://{FormatHostForUri(destinationHost)}:{Math.Clamp(destinationPort, 1, 65535)}";
    }

    private static string NormalizeName(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Enter a route name.");
        }

        return normalized;
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

    private static string NormalizeDestinationHost(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException("Enter a valid destination host.");
            }

            normalized = uri.Host;
        }

        normalized = normalized.Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('/') ||
            normalized.Contains(' ') ||
            normalized.Contains('{') ||
            normalized.Contains('}'))
        {
            throw new InvalidOperationException("Enter a destination IP or hostname such as 127.0.0.1 or app.internal.");
        }

        if (IPAddress.TryParse(normalized, out _) || Uri.CheckHostName(normalized) != UriHostNameType.Unknown)
        {
            return normalized;
        }

        throw new InvalidOperationException("Enter a valid destination IP or hostname.");
    }

    private static int NormalizePort(int value, string label)
    {
        if (value is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Enter a valid {label} from 1 to 65535.");
        }

        return value;
    }

    private static string FormatEndpointLabel(string host, int port) =>
        $"{FormatHostForUri(host)}:{Math.Clamp(port, 1, 65535)}";

    private static string FormatHostForUri(string host)
    {
        var normalized = (host ?? string.Empty).Trim();
        if (IPAddress.TryParse(normalized, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{normalized.Trim('[', ']')}]";
        }

        return normalized;
    }
}
