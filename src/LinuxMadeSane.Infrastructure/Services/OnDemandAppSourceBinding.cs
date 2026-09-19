// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Cloudflare;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class OnDemandAppSourceBinding(ILinuxCommandRunner commands)
{
    public sealed record Source(string InterfaceName, string Address);

    public static IReadOnlyList<Source> GetSources() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork &&
                           !IPAddress.IsLoopback(item.Address))
            .Select(item => new Source(nic.Name, item.Address.ToString())))
        .Distinct()
        .OrderBy(item => item.InterfaceName, StringComparer.Ordinal)
        .ThenBy(item => item.Address, StringComparer.Ordinal)
        .ToArray();

    public async Task<LocalHttpServiceEndpoint> ValidateAsync(
        LocalHttpServiceEndpoint endpoint,
        OnDemandAppProxyPreferences preferences,
        CancellationToken cancellationToken)
    {
        if (!preferences.ConnectAsLms)
        {
            return endpoint;
        }

        ValidateSelection(preferences, GetSources());
        await CheckCaddyAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endpoint.Host, timeout.Token);
        // Pin the checked destination so the probe and Caddy cannot resolve different routes.
        foreach (var address in addresses.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork))
        {
            var route = await commands.RunAsync(new LinuxCommandRequest(
                "ip", ["-j", "-4", "route", "get", address.ToString(), "from", preferences.SourceAddress],
                false, TimeSpan.FromSeconds(5), "Check On-Demand App source interface"), false, timeout.Token);
            if (route.ExitCode == 0 && RouteUsesInterface(route.StandardOutput, preferences.SourceInterface))
            {
                return endpoint with
                {
                    Host = address.ToString(),
                    IpAddress = address.ToString(),
                    Url = new UriBuilder(endpoint.Scheme, address.ToString(), endpoint.Port).Uri.AbsoluteUri
                };
            }
        }

        throw new InvalidOperationException(
            $"No IPv4 route to {endpoint.Host} uses {preferences.SourceInterface} from {preferences.SourceAddress}. " +
            "For Tailscale, select an app reachable through that interface. LMS has not changed your network routes.");
    }

    internal static void ValidateSelection(OnDemandAppProxyPreferences preferences, IReadOnlyList<Source> sources)
    {
        if (!sources.Any(source => source.InterfaceName == preferences.SourceInterface &&
                                   source.Address == preferences.SourceAddress))
        {
            throw new InvalidOperationException(
                "The selected source interface/address is not available. Select a current LMS IPv4 address; another interface will not be substituted.");
        }
    }

    internal static bool RouteUsesInterface(string json, string interfaceName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray().Any(route =>
                route.TryGetProperty("dev", out var device) && device.GetString() == interfaceName &&
                (!route.TryGetProperty("type", out var type) || type.GetString() == "unicast"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task CheckCaddyAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lms-caddy-source-{Guid.NewGuid():N}.caddy");
        try
        {
            await File.WriteAllTextAsync(path, """
                http://127.0.0.1:18088 {
                    reverse_proxy 127.0.0.1:18089 {
                        transport http {
                            local_address 127.0.0.1
                        }
                    }
                }
                """, cancellationToken);
            var result = await commands.RunAsync(new LinuxCommandRequest(
                "caddy", ["adapt", "--config", path, "--adapter", "caddyfile"],
                false, TimeSpan.FromSeconds(10), "Check Caddy source-address support without loading configuration"),
                false, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "This Caddy installation cannot enable source-address binding. Install a Caddy version supporting local_address " +
                    "(verified with 2.10.2) and test again. Current behaviour remains available; no live configuration was changed.");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static HttpClient CreateProbeClient(string sourceAddress)
    {
        var source = IPAddress.Parse(sourceAddress);
        return new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            SslOptions = new() { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
