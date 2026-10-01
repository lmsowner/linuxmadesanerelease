// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using LinuxMadeSane.Application.Contracts.HomeLab;

namespace LinuxMadeSane.Web.Services;

public sealed record HomeLabRecipeListenInterface(string InterfaceName, string Address, bool HasGateway = false, bool IsTunnel = false, bool IsTailnet = false);

public static class HomeLabRecipeNetworkSelection
{
    public static HomeLabRecipeListenInterface? RecommendListener(
        HomeLabRecipeNetworkRecommendation? recommendation, IEnumerable<HomeLabRecipeListenInterface> interfaces)
    {
        var available = interfaces.ToArray();
        if (recommendation?.ListenInterface == "tailnet")
        {
            var tailnet = available.Where(item => item.IsTailnet).ToArray();
            if (tailnet.Length > 0) return tailnet.Length == 1 ? tailnet[0] : null;
        }
        var lan = available.Where(IsLan).ToArray();
        if (lan.Length == 1) return lan[0];
        var defaults = lan.Where(item => item.HasGateway).ToArray();
        return defaults.Length == 1 ? defaults[0] : null;
    }

    public static string DescribeDirectRoute(IEnumerable<HomeLabRecipeListenInterface> interfaces)
    {
        var defaults = interfaces.Where(item => item.HasGateway)
            .Select(item => item.InterfaceName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return defaults.Length == 1
            ? $"The host currently has a gateway on {defaults[0]}; its routing table chooses the actual outbound interface."
            : "The host routing table chooses the outbound interface for each destination.";
    }

    private static bool IsLan(HomeLabRecipeListenInterface item)
    {
        if (item.IsTunnel || item.IsTailnet ||
            new[] { "docker", "br-", "veth", "virbr", "podman", "cni", "tun", "tap", "wg", "utun", "tailscale", "zt" }
                .Any(prefix => item.InterfaceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            !IPAddress.TryParse(item.Address, out var address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }
}
