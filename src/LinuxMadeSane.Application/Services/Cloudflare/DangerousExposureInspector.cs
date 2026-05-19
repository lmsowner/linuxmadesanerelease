// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Services.Cloudflare;

public static class DangerousExposureInspector
{
    private static readonly int[] DangerousPorts =
    [
        22,
        2375,
        2376,
        3306,
        5432,
        6379,
        9200,
        27017
    ];

    public static IReadOnlyList<ExposureWarning> Inspect(string serviceName, Uri localServiceUri)
    {
        var warnings = new List<ExposureWarning>();

        if (localServiceUri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            localServiceUri.Host.Equals("::", StringComparison.OrdinalIgnoreCase) ||
            localServiceUri.Host.Equals("[::]", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new ExposureWarning(
                "listen-all",
                "The target service listens on 0.0.0.0 or all interfaces. Confirm that it is intended to be reachable beyond the local machine.",
                true));
        }

        if (DangerousPorts.Contains(localServiceUri.Port))
        {
            warnings.Add(new ExposureWarning(
                $"port-{localServiceUri.Port}",
                $"Port {localServiceUri.Port} is commonly used for SSH, Docker, or database services. Review the target carefully before exposing it.",
                true));
        }

        var combinedName = $"{serviceName} {localServiceUri.AbsoluteUri}".ToLowerInvariant();
        if (combinedName.Contains("admin", StringComparison.Ordinal) ||
            combinedName.Contains("dashboard", StringComparison.Ordinal) ||
            combinedName.Contains("grafana", StringComparison.Ordinal) ||
            combinedName.Contains("portainer", StringComparison.Ordinal))
        {
            warnings.Add(new ExposureWarning(
                "admin-surface",
                "This looks like an admin or dashboard surface. Confirm that the hostname should be internet-reachable behind Cloudflare Access.",
                true));
        }

        return warnings;
    }
}
