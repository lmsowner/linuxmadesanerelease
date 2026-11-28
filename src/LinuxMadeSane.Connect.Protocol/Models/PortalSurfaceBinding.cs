// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.Net;
using System.Net.Sockets;

namespace LinuxMadeSane.Connect.Protocol.Models;

public sealed record PortalSurfaceBinding(string Address, string Interface, string Protocol, int Port)
{
    private static readonly System.Net.IPNetwork[] NonPublicV6 =
    [
        System.Net.IPNetwork.Parse("2001::/23"), System.Net.IPNetwork.Parse("2001:db8::/32"),
        System.Net.IPNetwork.Parse("2002::/16"), System.Net.IPNetwork.Parse("3fff::/20")
    ];

    public static bool IsPublicInterface(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork) return PortalDirectNetworkPolicy.IsPublic(ip);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId == 0 &&
            (ip.GetAddressBytes()[0] & 0xe0) == 0x20 && !NonPublicV6.Any(network => network.Contains(ip));
    }
}

public sealed record PortalSurfaceFinding(string Category, string Status, string Summary, string Evidence, string Recommendation);
