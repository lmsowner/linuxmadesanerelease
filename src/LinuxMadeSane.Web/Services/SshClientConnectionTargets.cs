// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Security;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public sealed record SshClientConnectionTarget(string Host, string Label, IReadOnlyList<int> Ports)
{
    public bool IsDiscoveredPublicAddress { get; init; }
}

public static class SshClientConnectionTargets
{
    public static IReadOnlyList<SshClientConnectionTarget> Build(SshAdminOverview overview, string browserHost,
        IReadOnlyList<IPAddress> browserAddresses, IReadOnlyDictionary<string, string>? interfaceNames = null,
        string? discoveredPublicIp = null)
    {
        var result = new List<SshClientConnectionTarget>();
        var ports = overview.EffectiveSettings.Ports.Where(p => p is > 0 and <= 65535).Distinct().ToArray();
        var bindings = overview.EffectiveSettings.ListenAddresses
            .Select(value => IPEndPoint.TryParse(value, out var endpoint) ? endpoint : null)
            .Where(endpoint => endpoint is not null).ToArray();
        foreach (var host in overview.HostAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(host, out var address) || IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast) continue;
            var matchingPorts = bindings.Length == 0 ? ports : bindings.Where(binding => binding!.Address.Equals(address) ||
                binding.Address.Equals(IPAddress.Any) && address.AddressFamily == AddressFamily.InterNetwork ||
                binding.Address.Equals(IPAddress.IPv6Any) && address.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(binding => binding!.Port).Where(port => port is > 0 and <= 65535).Distinct().ToArray();
            if (matchingPorts.Length == 0) continue;
            var label = interfaceNames?.GetValueOrDefault(host);
            result.Add(new(address.ToString(), string.IsNullOrWhiteSpace(label) ? host : $"{label} · {host}", matchingPorts));
        }
        // A web hostname is a direct SSH candidate only if DNS maps it to a reported SSH-bound address.
        var matchingTargets = result.Where(target => browserAddresses.Any(ip => ip.ToString().Equals(target.Host, StringComparison.OrdinalIgnoreCase))).ToArray();
        var browserMapsToSsh = browserAddresses.Count > 0 && browserAddresses.All(ip => result.Any(target => IPAddress.Parse(target.Host).Equals(ip)));
        if (IsValidHost(browserHost) && browserMapsToSsh && matchingTargets.Length > 0 && !result.Any(target => target.Host.Equals(browserHost, StringComparison.OrdinalIgnoreCase)))
            result.Insert(0, new(browserHost, $"This browser's hostname · {browserHost}", matchingTargets.SelectMany(target => target.Ports).Distinct().ToArray()));
        if (IsValidHost(overview.HostName) && result.Count > 0 && !result.Any(target => target.Host.Equals(overview.HostName, StringComparison.OrdinalIgnoreCase)))
            result.Add(new(overview.HostName, $"Server name · {overview.HostName} (requires DNS)", result.SelectMany(target => target.Ports).Distinct().ToArray()));
        if (discoveredPublicIp is not null && PublicIpAddressRules.IsPublic(discoveredPublicIp) && ports.Length > 0)
        {
            var address = IPAddress.Parse(discoveredPublicIp).ToString();
            var existing = result.FindIndex(target => target.Host.Equals(address, StringComparison.OrdinalIgnoreCase));
            var target = new SshClientConnectionTarget(address,
                $"Public IP · {address} (SSH reachability not tested)", existing >= 0 ? result[existing].Ports : ports)
                { IsDiscoveredPublicAddress = true };
            if (existing >= 0) result[existing] = target;
            else result.Add(target);
        }
        return result.OrderBy(target => browserMapsToSsh && target.Host.Equals(browserHost, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
    }

    public static bool IsValidHost(string? host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        (IPAddress.TryParse(host, out _) || Regex.IsMatch(host, @"^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant));

    public static string Command(string machine, string user, string host, int port, bool windows)
    {
        if (!IsValidHost(host) || port is <= 0 or > 65535 || !Regex.IsMatch(user, @"^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Choose a valid SSH hostname or IP address, Linux username and port.");
        return $"ssh -o IdentitiesOnly=yes -i {ClientSshKeyCommands.PrivateKeyPath(machine, user, windows)} -p {port} \"{user}@{host}\"";
    }
}
