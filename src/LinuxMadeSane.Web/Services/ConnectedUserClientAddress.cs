// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;

namespace LinuxMadeSane.Web.Services;

public static class ConnectedUserClientAddress
{
    public const string HeaderName = "X-LMS-Client-IP";
    private const string ItemKey = "LmsConnectedUserClientAddress";

    // Capture before forwarded-header middleware changes the transport peer address.
    // This address is for connection reporting only, never for authentication or access decisions.
    public static void Capture(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer?.IsIPv4MappedToIPv6 == true) peer = peer.MapToIPv4();
        if (peer is null || !IPAddress.IsLoopback(peer)) return;
        var header = context.Request.Headers[HeaderName];
        if (header.Count == 1 && IPAddress.TryParse(header[0], out var client) &&
            !client.Equals(IPAddress.Any) && !client.Equals(IPAddress.IPv6Any))
            context.Items[ItemKey] = client;
    }

    public static IPAddress? GetAddress(HttpContext? context) =>
        context?.Items[ItemKey] is IPAddress client ? client : context?.Connection.RemoteIpAddress;
}
