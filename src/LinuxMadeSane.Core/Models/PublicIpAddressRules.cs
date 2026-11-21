// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Net;
using System.Net.Sockets;

namespace LinuxMadeSane.Core.Models;

public static class PublicIpAddressRules
{
    public static bool IsPublic(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.IsIPv4MappedToIPv6 || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
                bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2 ||
                bytes[0] == 198 && bytes[1] is 18 or 19 || bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
                bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        // Globally routed IPv6 unicast, excluding the documentation prefix.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }
}
