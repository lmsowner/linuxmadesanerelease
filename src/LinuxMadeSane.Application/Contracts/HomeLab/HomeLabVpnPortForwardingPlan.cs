// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.HomeLab;

public static class HomeLabVpnPortForwardingPlan
{
    public const string RequiredSelection = "Required for qBittorrent/P2P";
    public const string OffSelection = "Off (streaming only)";

    public static IReadOnlyDictionary<string, string> Build(
        string selection,
        string provider,
        string protocol,
        string? pastedConfiguration)
    {
        if (selection.Equals(OffSelection, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string> { ["VPN_PORT_FORWARDING"] = "off" };
        }
        if (!selection.Equals(RequiredSelection, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a supported incoming P2P port option.");
        }

        var forwardingProvider = provider switch
        {
            "ProtonVPN" => "protonvpn",
            "Private Internet Access" => "private internet access",
            _ => throw new InvalidOperationException($"{provider} does not expose automatic port forwarding through this LMS definition. Choose '{OffSelection}' or use a supported P2P provider.")
        };

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (provider.Equals("ProtonVPN", StringComparison.OrdinalIgnoreCase))
        {
            if (pastedConfiguration?.Contains("-FREE#", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException("This Proton profile targets a FREE server. Proton FREE servers do not support P2P or port forwarding. Generate a paid P2P server profile with NAT-PMP enabled, then paste that profile.");
            }
            if (protocol.Equals("wireguard", StringComparison.OrdinalIgnoreCase) &&
                pastedConfiguration is not null &&
                !pastedConfiguration.Contains("NAT-PMP (Port Forwarding) = on", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This Proton WireGuard profile does not declare NAT-PMP port forwarding. Generate a P2P server profile with NAT-PMP enabled, then paste that profile.");
            }
            if (pastedConfiguration is null)
            {
                result["PORT_FORWARD_ONLY"] = "on";
            }
        }

        result["VPN_PORT_FORWARDING"] = "on";
        result["VPN_PORT_FORWARDING_PROVIDER"] = forwardingProvider;
        result["VPN_PORT_FORWARDING_UP_COMMAND"] = "/bin/sh -c 'wget -O- -nv --retry-connrefused --tries=10 --post-data \"json={\\\"listen_port\\\":{{PORT}},\\\"current_network_interface\\\":\\\"{{VPN_INTERFACE}}\\\",\\\"random_port\\\":false,\\\"upnp\\\":false}\" http://127.0.0.1:8080/api/v2/app/setPreferences'";
        result["VPN_PORT_FORWARDING_DOWN_COMMAND"] = "/bin/sh -c 'wget -O- -nv --retry-connrefused --tries=5 --post-data \"json={\\\"listen_port\\\":0,\\\"current_network_interface\\\":\\\"lo\\\"}\" http://127.0.0.1:8080/api/v2/app/setPreferences || true'";
        return result;
    }
}
