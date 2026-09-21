// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Net;
using System.Net.Sockets;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class HomeLabQbittorrentWebUiAccess
{
    public static bool TryBuildDockerGatewaySubnet(string output, out string subnet)
    {
        if (TryParseDockerGatewayAddress(output, out var gatewayAddress) &&
            IPAddress.TryParse(gatewayAddress, out var address))
        {
            subnet = $"{address}/{(address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)}";
            return true;
        }

        subnet = string.Empty;
        return false;
    }

    public static bool TryParseDockerGatewayAddress(string output, out string gatewayAddress)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(line, out var address))
            {
                continue;
            }

            gatewayAddress = address.ToString();
            return true;
        }

        gatewayAddress = string.Empty;
        return false;
    }
}
