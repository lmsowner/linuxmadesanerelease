// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.Json;
using LinuxMadeSane.Application.Contracts.HomeLab;
using LinuxMadeSane.Core.Models.HomeLab;

namespace LinuxMadeSane.Infrastructure.Services;

public static class HomeLabConnectionAddressPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<HomeLabConnectionAddress> Build(
        HomeLabAppInstallation source,
        IEnumerable<HomeLabAppInstallation> candidates)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(target => target.Id != source.Id &&
                             !target.AppId.Equals("vpn-gateway", StringComparison.OrdinalIgnoreCase))
            .Select(target => BuildAddress(source, target))
            .Where(address => address is not null)
            .Cast<HomeLabConnectionAddress>()
            .OrderByDescending(address => address.IsReachable)
            .ThenBy(address => address.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HomeLabConnectionAddress? BuildAddress(
        HomeLabAppInstallation source,
        HomeLabAppInstallation target)
    {
        var app = HomeLabCatalog.GetApp(target.AppId);
        var ports = DeserializePorts(target.PortMappingsJson);
        var primaryPort = app.Ports.FirstOrDefault(port => port.Primary) ?? app.Ports.FirstOrDefault();
        var binding = primaryPort is null
            ? ports.FirstOrDefault()
            : ports.FirstOrDefault(port => port.Name.Equals(primaryPort.Name, StringComparison.OrdinalIgnoreCase));
        if (binding is null || binding.ContainerPort <= 0)
        {
            return null;
        }

        var sourceNamespace = GetContainerNamespace(source.NetworkMode);
        var targetNamespace = GetContainerNamespace(target.NetworkMode);
        var sharesVpnNamespace = sourceNamespace.Length > 0 &&
                                 sourceNamespace.Equals(targetNamespace, StringComparison.OrdinalIgnoreCase);
        var sharesDockerNetwork = sourceNamespace.Length == 0 &&
                                  targetNamespace.Length == 0 &&
                                  source.NetworkName.Equals(target.NetworkName, StringComparison.OrdinalIgnoreCase);

        var reachable = sharesVpnNamespace || sharesDockerNetwork;
        var host = sharesVpnNamespace
            ? "127.0.0.1"
            : sharesDockerNetwork
                ? target.ContainerName
                : string.Empty;
        var isHttp = binding.Name.Equals("web", StringComparison.OrdinalIgnoreCase);
        var value = reachable
            ? isHttp ? $"http://{host}:{binding.ContainerPort}" : $"{host}:{binding.ContainerPort}"
            : string.Empty;

        var networkPath = sharesVpnNamespace
            ? "Shared VPN gateway"
            : sharesDockerNetwork
                ? "Shared Docker network"
                : "Network change required";
        var portMapping = reachable
            ? "Not required — use the internal listener"
            : "A host port mapping will not join isolated container networks";
        var detail = sharesVpnNamespace
            ? $"{source.DisplayName} and {target.DisplayName} share the same VPN network namespace. Loopback reaches the target without exposing another host port."
            : sharesDockerNetwork
                ? $"Docker resolves {target.ContainerName} on the shared LMS network. Container IP addresses are temporary and should not be saved."
                : "The apps do not currently share a VPN gateway or Docker network. Route them through the same LMS VPN Gateway or deploy them in one recipe before configuring this connection.";

        return new HomeLabConnectionAddress(
            target.Id,
            target.AppId,
            target.DisplayName,
            DescribePurpose(target.AppId),
            host,
            binding.ContainerPort,
            value,
            reachable,
            networkPath,
            portMapping,
            detail);
    }

    private static string GetContainerNamespace(string networkMode) =>
        networkMode.StartsWith("container:", StringComparison.OrdinalIgnoreCase)
            ? networkMode["container:".Length..].Trim().TrimStart('/')
            : string.Empty;

    private static string DescribePurpose(string appId) => appId.ToLowerInvariant() switch
    {
        "qbittorrent" => "Download client",
        "prowlarr" => "Indexer manager",
        "sonarr" => "TV library manager",
        "radarr" => "Movie library manager",
        "seerr" => "Request manager",
        "wordpress-database" => "Database",
        _ => "App connection"
    };

    private static IReadOnlyList<PortBinding> DeserializePorts(string json) =>
        JsonSerializer.Deserialize<List<PortBinding>>(json, JsonOptions) ?? [];

    private sealed record PortBinding(string Name, int ContainerPort, int HostPort);
}
