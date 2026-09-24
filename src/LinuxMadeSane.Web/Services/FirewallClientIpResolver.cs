// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;

namespace LinuxMadeSane.Web.Services;

// Used only to prefill a user-reviewed firewall rule, never for authentication or network trust.
public static class FirewallClientIpResolver
{
    public static bool ShouldCheckCloudflare(string requestHost) =>
        !IsLoopbackHost(requestHost) && !IPAddress.TryParse(requestHost.Trim('[', ']'), out _);

    public static string? Resolve(string? connectionAddress, string requestHost, string? browserAddress)
    {
        if (NormalizeBrowserAddress(browserAddress) is { } browserIp)
        {
            return browserIp;
        }

        var address = ParseAddress(connectionAddress);
        if (address is null || (IPAddress.IsLoopback(address) && !IsLoopbackHost(requestHost)))
        {
            return null;
        }

        return address.ToString();
    }

    public static string? NormalizeBrowserAddress(string? value)
    {
        var address = ParseAddress(value);
        return address is null || IPAddress.IsLoopback(address) ? null : address.ToString();
    }

    private static IPAddress? ParseAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            return null;
        }

        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast)
        {
            return null;
        }

        // UFW accepts the IPv6 address without the connection's interface scope.
        return new IPAddress(address.GetAddressBytes());
    }

    private static bool IsLoopbackHost(string requestHost) =>
        requestHost.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(requestHost.Trim('[', ']'), out var address) &&
         IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
}
