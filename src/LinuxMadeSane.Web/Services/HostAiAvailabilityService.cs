// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace LinuxMadeSane.Web.Services;

public sealed record HostAiRoute(string Url, string Transport, bool Reachable = false);
public sealed record HostAiAvailability(bool AiEnabled, bool LocalEngineRunning, bool SharingEnabled,
    bool SharingReady, IReadOnlyList<HostAiRoute> Routes, string? Detail = null, bool LocalEngineInstalled = false);

public sealed class HostAiAvailabilityService(IAiProviderSettingsStore providers, IAiProviderRegistry registry,
    ILocalAiEngineStore engine, LocalAiPeerSharingService sharing, IHttpClientFactory clients, IServer server, IOllamaRuntimeService? runtimeService = null,
    IDockerAiEngineDiscoveryService? dockerEngines = null)
{
    public async Task<HostAiAvailability> GetLocalAsync(CancellationToken token)
    {
        var installation = InspectLocalInstallationAsync(token);
        var configured = await providers.ListAsync(token);
        var enabled = configured.Any(p => p.IsEnabled && registry.FindDefinition(p.ProviderType) is { IsRuntimeImplemented: true } definition &&
            (!definition.RequiresApiKey || !string.IsNullOrWhiteSpace(p.ApiKeySecretReference)));
        var settings = await engine.GetSettingsAsync(token);
        var shared = await sharing.GetStatusAsync(token);
        var running = false;
        var modelAvailable = false;
        var routes = new List<HostAiRoute>();
        if (Uri.TryCreate(settings.RuntimeEndpoint, UriKind.Absolute, out var runtime) && runtime.Scheme is "http" or "https" &&
            IsLocalRuntime(runtime))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var response = await clients.CreateClient("HostAiAvailability").GetAsync(new Uri(runtime.AbsoluteUri.TrimEnd('/') + "/v1/models"), timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                    running = json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array;
                    modelAvailable = running && data.EnumerateArray().Any(m => m.TryGetProperty("id", out var id) && id.GetString() == settings.DefaultModelId);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
            foreach (var address in server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Port != runtime.Port || uri.IsLoopback ||
                    !IPAddress.TryParse(uri.Host, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) continue;
                var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(ip)));
                var tailnet = network is not null && (network.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) || network.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase));
                routes.Add(new(uri.GetLeftPart(UriPartial.Authority), tailnet ? "Tailnet HTTP" : $"Direct {uri.Scheme.ToUpperInvariant()}"));
            }
        }
        return new(enabled, running, shared.Enabled && shared.HasAccessKey, shared.Enabled && shared.HasAccessKey && modelAvailable, routes,
            LocalEngineInstalled: await installation);
    }

    private async Task<bool> InspectLocalInstallationAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var native = InspectNativeAsync(timeout.Token);
        var docker = InspectDockerAsync(timeout.Token);
        await Task.WhenAll(native, docker);
        token.ThrowIfCancellationRequested();
        return native.Result || docker.Result;
    }

    private async Task<bool> InspectNativeAsync(CancellationToken token)
    {
        try { return runtimeService is not null && (await runtimeService.InspectAsync(token)).IsInstalled; }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or HttpRequestException or NotSupportedException) { return false; }
    }

    private async Task<bool> InspectDockerAsync(CancellationToken token)
    {
        try { return dockerEngines is not null && (await dockerEngines.GetWorkspaceAsync(token)).DiscoveredEngines.Count > 0; }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or HttpRequestException or NotSupportedException) { return false; }
    }

    private static bool IsLocalRuntime(Uri endpoint) => endpoint.IsLoopback ||
        endpoint.Host.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(endpoint.Host, out var address) && NetworkInterface.GetAllNetworkInterfaces()
            .Any(network => network.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(address))));

    public async Task<HostAiAvailability> GetRemoteAsync(string? verifiedWebUrl, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(verifiedWebUrl)) return new(false, false, false, false, [],
            "AI status unknown. SSH access alone does not provide a direct AI HTTP route. Configure LAN or tailnet connectivity.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await clients.CreateClient("HostAiAvailability").GetAsync(verifiedWebUrl.TrimEnd('/') + "/healthz/ai", timeout.Token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<HostAiAvailability>(timeout.Token);
            if (result is null || result.Routes is null) throw new JsonException();
            var checkedRoutes = new List<HostAiRoute>();
            foreach (var route in result.Routes.Take(8))
            {
                var reachable = false;
                if (Uri.TryCreate(route.Url, UriKind.Absolute, out var uri) && !uri.IsLoopback && uri.Scheme is "http" or "https")
                {
                    try
                    {
                        using var routeTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        routeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        using var probe = await clients.CreateClient("HostAiAvailability").GetAsync(uri, routeTimeout.Token);
                        using var json = JsonDocument.Parse(await probe.Content.ReadAsStringAsync(routeTimeout.Token));
                        reachable = probe.IsSuccessStatusCode && json.RootElement.TryGetProperty("service", out var service) && service.GetString() == "Linux Made Sane AI Service";
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                    checkedRoutes.Add(route with { Reachable = reachable });
                }
            }
            return result with { Routes = checkedRoutes, Detail = checkedRoutes.Any(r => r.Reachable) ? "Route reachable from this LMS server; access key verification is required before use." : "No advertised AI route is reachable from this LMS server. Web or SSH access does not prove AI connectivity." };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        {
            token.ThrowIfCancellationRequested();
            return new(false, false, false, false, [], "AI status unavailable. Update the remote LMS host for AI discovery, or check its direct HTTP/HTTPS connectivity.");
        }
    }
}
