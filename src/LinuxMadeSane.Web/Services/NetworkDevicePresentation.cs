// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Net;
using System.Net.Sockets;
using LinuxMadeSane.Application.Contracts.Infrastructure;
using LinuxMadeSane.Infrastructure.Services.Infrastructure;
namespace LinuxMadeSane.Web.Services;

public sealed record NetworkInterfaceDevices(string Interface, IReadOnlyList<string> Subnets, IReadOnlyList<NetworkDevice> Devices);
public static class NetworkDevicePresentation
{
    public static string Name(NetworkDevice device) => !string.IsNullOrWhiteSpace(device.FriendlyName) ? device.FriendlyName :
        !string.IsNullOrWhiteSpace(device.Hostname) && !IPAddress.TryParse(device.Hostname, out _) ? device.Hostname : PrimaryAddress(device);
    public static string PrimaryAddress(NetworkDevice device) => device.Addresses.FirstOrDefault(value =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) ?? device.Addresses.FirstOrDefault() ?? "—";
    public static IReadOnlyList<NetworkInterfaceDevices> Group(DeviceInventory inventory)
    {
        string Interface(NetworkDevice device)
        {
            if (!string.IsNullOrWhiteSpace(device.Interface)) return device.Interface;
            var matches = inventory.InterfaceNetworks.Where(network => device.Addresses.Any(value =>
            {
                var parts = network.Network.Split('/');
                return parts.Length == 2 && int.TryParse(parts[1], out var prefix) && prefix is >= 0 and <= 32 &&
                    IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
                    KeaDhcpManagementService.NetworkPrefix(ip, prefix) == network.Network;
            })).Select(x => x.Interface).Distinct().ToArray();
            return matches.Length == 1 ? matches[0] : "Interface not determined";
        }
        return inventory.Devices.GroupBy(Interface).OrderBy(x => x.Key == "Interface not determined" ? 1 : 0)
            .ThenByDescending(x => x.Count()).ThenBy(x => x.Key)
            .Select(group => new NetworkInterfaceDevices(group.Key,
                inventory.InterfaceNetworks.Where(network => network.Interface == group.Key).Select(network => network.Network)
                    .Concat(group.Select(device => device.Subnet).Where(subnet => !string.IsNullOrWhiteSpace(subnet))).Distinct().Order().ToArray(),
                group.OrderBy(Name, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
    }
}
