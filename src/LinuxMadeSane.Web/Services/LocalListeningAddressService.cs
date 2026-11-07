// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using LinuxMadeSane.Core.Abstractions;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LinuxMadeSane.Web.Services;

public interface ILocalListeningAddressService
{
    IReadOnlyList<string> Read();
}

public sealed class LocalListeningAddressService : ILocalListeningAddressService, IHostIpv4AddressService
{
    public IReadOnlyList<string> Read()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var bindings = properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners())
            .Select(endpoint => endpoint.Address);
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => OperatingSystem.IsLinux()
                ? IsActiveLinuxInterface(network.Name, network.OperationalStatus)
                : network.OperationalStatus == OperationalStatus.Up)
            .Where(network => OperatingSystem.IsLinux()
                ? IsEligibleLinuxInterface(network.Name)
                : network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address);
        return Resolve(bindings, interfaces);
    }

    public HostIpv4Addresses ReadPrimaryIpv4()
    {
        var interfaces=NetworkInterface.GetAllNetworkInterfaces()
            .Where(n=>OperatingSystem.IsLinux()?IsActiveLinuxInterface(n.Name,n.OperationalStatus):n.OperationalStatus==OperationalStatus.Up)
            .Where(n=>OperatingSystem.IsLinux()?IsEligibleLinuxInterface(n.Name):n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Select(n=>(n.Name,Addresses:(IReadOnlyList<IPAddress>)n.GetIPProperties().UnicastAddresses.Select(a=>a.Address).ToArray())).ToArray();
        string? defaultInterface=null;
        if(OperatingSystem.IsLinux())
            try {defaultInterface=DefaultIpv4Interface(File.ReadLines("/proc/net/route"));}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){}
        return SelectPrimaryIpv4(interfaces,defaultInterface);
    }
    internal static string? DefaultIpv4Interface(IEnumerable<string> lines)=>lines
        .Select(line=>line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries))
        .Where(f=>f.Length>7&&f[1]=="00000000"&&f[7]=="00000000"&&uint.TryParse(f[3],System.Globalization.NumberStyles.HexNumber,null,out var flags)&&(flags&1)!=0)
        .OrderBy(f=>long.TryParse(f[6],out var metric)?metric:long.MaxValue).Select(f=>f[0]).FirstOrDefault();
    internal static HostIpv4Addresses SelectPrimaryIpv4(IEnumerable<(string Name,IReadOnlyList<IPAddress> Addresses)> interfaces,string? defaultInterface)
    {
        var eligible=interfaces.SelectMany(n=>n.Addresses.Select(a=>(n.Name,Address:Normalize(a))))
            .Where(n=>n.Address.AddressFamily==AddressFamily.InterNetwork&&IsUsableAddress(n.Address)).ToArray();
        var main=eligible.Where(n=>n.Name!="tailscale0")
            .OrderBy(n=>n.Name==defaultInterface?0:1).ThenBy(n=>n.Name,StringComparer.Ordinal)
            .ThenBy(n=>Convert.ToHexString(n.Address.GetAddressBytes()),StringComparer.Ordinal).FirstOrDefault().Address;
        var tail=eligible.FirstOrDefault(n=>n.Name=="tailscale0").Address;
        return new(main?.ToString(),tail?.ToString());
    }

    internal static IReadOnlyList<string> Resolve(IEnumerable<IPAddress> bindings, IEnumerable<IPAddress> interfaceAddresses)
    {
        var local = interfaceAddresses.Select(Normalize).Where(IsUsableAddress).Distinct().ToArray();
        return bindings.Select(Normalize)
            .SelectMany(address => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
                ? local.Where(candidate => candidate.AddressFamily == address.AddressFamily)
                : [address])
            // A specific socket may bind to loopback, broadcast, or a virtual NIC.
            // Only addresses assigned to a physical NIC or Tailscale belong here.
            .Where(local.Contains)
            .Distinct()
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(address => Convert.ToHexString(address.GetAddressBytes()), StringComparer.Ordinal)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .Select(address => address.ToString()).ToArray();
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // Linux exposes a hardware device for physical NICs (including a VM's NIC),
    // unlike software bridges, container veth pairs, loopback, and VPN adapters.
    internal static bool IsPhysicalLinuxInterface(string name, string interfaceRoot = "/sys/class/net")
        => Directory.Exists(Path.Combine(interfaceRoot, name, "device"));

    internal static bool IsEligibleLinuxInterface(string name, string interfaceRoot = "/sys/class/net")
        => name.Equals("tailscale0", StringComparison.Ordinal) || IsPhysicalLinuxInterface(name, interfaceRoot);

    internal static bool IsActiveLinuxInterface(string name, OperationalStatus status, string interfaceRoot = "/sys/class/net")
    {
        if (status == OperationalStatus.Up) return true;
        // TUN devices normally report UNKNOWN even when administratively UP.
        if (name != "tailscale0" || status != OperationalStatus.Unknown) return false;
        try { return (Convert.ToUInt32(File.ReadAllText(Path.Combine(interfaceRoot, name, "flags")).Trim(), 16) & 1) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        { return false; }
    }

    private static bool IsUsableAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal &&
                   !address.IsIPv6SiteLocal && (!address.IsIPv6UniqueLocal || IsTailscaleIpv6(address)) && !address.IsIPv6Multicast;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsTailscaleIpv6(IPAddress address)
        => address.GetAddressBytes().AsSpan(0, 6).SequenceEqual(new byte[] { 0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0 });
}
