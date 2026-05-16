// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Net;
using System.Net.Sockets;

namespace LinuxMadeSane.Application.Services;

public static class LocalRequestTargetEvaluator
{
    public static bool IsLocal(string? requestHost)
    {
        var normalizedHost = requestHost?.Trim().TrimEnd('.')
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            return false;
        }

        if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(normalizedHost, out var address))
        {
            return false;
        }

        address = Normalize(address);
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal || IsUniqueLocalIpv6(address);
    }

    private static bool IsUniqueLocalIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
