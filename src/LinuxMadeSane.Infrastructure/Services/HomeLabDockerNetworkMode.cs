// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Services;

internal static class HomeLabDockerNetworkMode
{
    private const string ContainerPrefix = "container:";

    public static bool UsesContainerNamespace(
        string? actualNetworkMode,
        string gatewayContainerName,
        string? gatewayContainerId)
    {
        if (string.IsNullOrWhiteSpace(actualNetworkMode) ||
            !actualNetworkMode.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actualTarget = Normalize(actualNetworkMode[ContainerPrefix.Length..]);
        return actualTarget.Equals(Normalize(gatewayContainerName), StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(gatewayContainerId) &&
               actualTarget.Equals(Normalize(gatewayContainerId), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Trim().TrimStart('/');
}
