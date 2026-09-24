// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LinuxMadeSane.Web.Components.Ui;

public enum UiNetworkInterfaceIpScope
{
    AllInterfaces,
    Loopback,
    PrivateLan,
    Tailscale,
    LinkLocal,
    Public,
    Ipv6,
    Saved
}

public sealed record UiNetworkInterfaceIpOption(
    string Address,
    string InterfaceName,
    string Detail,
    UiNetworkInterfaceIpScope Scope)
{
    public string ScopeLabel => Scope switch
    {
        UiNetworkInterfaceIpScope.AllInterfaces => "All interfaces",
        UiNetworkInterfaceIpScope.Loopback => "Local only",
        UiNetworkInterfaceIpScope.PrivateLan => "Private LAN",
        UiNetworkInterfaceIpScope.Tailscale => "Tailscale",
        UiNetworkInterfaceIpScope.LinkLocal => "Link local",
        UiNetworkInterfaceIpScope.Public => "PUBLIC",
        UiNetworkInterfaceIpScope.Ipv6 => "IPv6",
        _ => "Saved value"
    };
}

public static class UiNetworkInterfaceIpCatalog
{
    public static IReadOnlyList<UiNetworkInterfaceIpOption> GetOptions(
        bool ipv4Only,
        bool includeAllIpv4,
        bool includeLoopback)
    {
        var options = new List<UiNetworkInterfaceIpOption>();
        if (includeAllIpv4)
        {
            options.Add(new("0.0.0.0", "All IPv4 interfaces", "Every IPv4 address on this host", UiNetworkInterfaceIpScope.AllInterfaces));
        }
        if (includeLoopback)
        {
            options.Add(new("127.0.0.1", "Loopback", "Local machine and local TCP bridges", UiNetworkInterfaceIpScope.Loopback));
        }

        var seen = options.Select(static option => option.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(static item => item.OperationalStatus == OperationalStatus.Up)
                         .OrderBy(static item => item.NetworkInterfaceType == NetworkInterfaceType.Loopback ? 1 : 0)
                         .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                IPInterfaceProperties properties;
                try
                {
                    properties = networkInterface.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                foreach (var addressInfo in properties.UnicastAddresses)
                {
                    var address = Normalize(addressInfo.Address);
                    if (!IsSelectable(address, ipv4Only) || !seen.Add(address.ToString()))
                    {
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(networkInterface.Name)
                        ? networkInterface.NetworkInterfaceType.ToString()
                        : networkInterface.Name.Trim();
                    var family = address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                    options.Add(new(
                        address.ToString(),
                        name,
                        $"{networkInterface.Description} · {family}",
                        Classify(address)));
                }
            }
        }
        catch
        {
            // Interface discovery is best effort. Saved values remain selectable in the picker.
        }

        return options;
    }

    public static bool IsPublicIpv4Address(string value) =>
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork &&
        Classify(address) == UiNetworkInterfaceIpScope.Public;

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsSelectable(IPAddress address, bool ipv4Only) =>
        (address.AddressFamily == AddressFamily.InterNetwork || (!ipv4Only && address.AddressFamily == AddressFamily.InterNetworkV6)) &&
        !Equals(address, IPAddress.Any) &&
        !Equals(address, IPAddress.IPv6Any) &&
        !Equals(address, IPAddress.None) &&
        !Equals(address, IPAddress.IPv6None) &&
        !IPAddress.IsLoopback(address) &&
        !address.IsIPv6LinkLocal;

    private static UiNetworkInterfaceIpScope Classify(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return UiNetworkInterfaceIpScope.Loopback;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return UiNetworkInterfaceIpScope.Ipv6;
        }

        var bytes = address.GetAddressBytes();
        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
        {
            return UiNetworkInterfaceIpScope.Tailscale;
        }
        if (bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168))
        {
            return UiNetworkInterfaceIpScope.PrivateLan;
        }
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return UiNetworkInterfaceIpScope.LinkLocal;
        }

        return UiNetworkInterfaceIpScope.Public;
    }
}
