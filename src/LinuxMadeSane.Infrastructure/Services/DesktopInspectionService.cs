// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DesktopInspectionService(
    ILinuxCommandRunner commandRunner,
    IPackageManagementService packageManagementService,
    IServiceManagementService serviceManagementService,
    ISessionConfigurationService sessionConfigurationService) : IDesktopInspectionService
{
    public async Task<DesktopInspectionReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        var osInfo = await ReadOsInfoAsync(cancellationToken);
        var kernelInfo = await commandRunner.RunAsync(
            new LinuxCommandRequest("uname", ["-r"], false, TimeSpan.FromSeconds(10), "Read kernel version"),
            dryRun: false,
            cancellationToken);

        var packages = await packageManagementService.InspectAsync(RdpOptimizerCatalog.RelevantPackages, cancellationToken);
        var services = await serviceManagementService.InspectAsync(RdpOptimizerCatalog.RelevantServices, cancellationToken);
        var sessionConfiguration = await sessionConfigurationService.InspectAsync(cancellationToken);

        var installedDesktopEnvironments = BuildInstalledDesktopEnvironments(packages);
        var xrdpInstalled = IsInstalled(packages, "xrdp");
        var xfceInstalled = IsInstalled(packages, "xfce4");
        var gnomeInstalled = IsInstalled(packages, "gnome-shell") || IsInstalled(packages, "ubuntu-desktop") || IsInstalled(packages, "ubuntu-desktop-minimal");
        var xrdpService = services.FirstOrDefault(item => item.Name.Equals("xrdp.service", StringComparison.OrdinalIgnoreCase));

        return new DesktopInspectionReport(
            osInfo.DistributionName,
            osInfo.UbuntuVersion,
            kernelInfo.StandardOutput.Trim(),
            sessionConfiguration.DisplayManager,
            installedDesktopEnvironments,
            osInfo.DistributionName.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase),
            xrdpInstalled,
            xfceInstalled,
            gnomeInstalled,
            xrdpService?.IsEnabled ?? false,
            xrdpService?.IsActive ?? false,
            sessionConfiguration,
            packages,
            services,
            BuildLikelyGains(gnomeInstalled, xfceInstalled, sessionConfiguration.XrdpUsesXfce),
            BuildLikelyIssues(osInfo.UbuntuVersion, xrdpInstalled, xfceInstalled, sessionConfiguration, services));
    }

    private async Task<(string DistributionName, string UbuntuVersion)> ReadOsInfoAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "/bin/sh",
                ["-lc", ". /etc/os-release && printf '%s\t%s\n' \"$NAME\" \"$VERSION_ID\""],
                false,
                TimeSpan.FromSeconds(10),
                "Read distribution metadata"),
            dryRun: false,
            cancellationToken);

        var parts = result.StandardOutput.Trim().Split('\t', StringSplitOptions.TrimEntries);
        return (parts.ElementAtOrDefault(0) ?? "Unknown Linux", parts.ElementAtOrDefault(1) ?? "unknown");
    }

    private static IReadOnlyList<string> BuildInstalledDesktopEnvironments(IReadOnlyList<PackageState> packages)
    {
        var desktops = new List<string>();
        if (IsInstalled(packages, "xfce4"))
        {
            desktops.Add("XFCE");
        }

        if (IsInstalled(packages, "gnome-shell") || IsInstalled(packages, "ubuntu-desktop") || IsInstalled(packages, "ubuntu-desktop-minimal"))
        {
            desktops.Add("GNOME");
        }

        if (IsInstalled(packages, "kde-plasma-desktop") || IsInstalled(packages, "sddm"))
        {
            desktops.Add("KDE Plasma");
        }

        if (desktops.Count == 0)
        {
            desktops.Add("Headless / console");
        }

        return desktops;
    }

    private static OptimizationGainEstimate BuildLikelyGains(bool gnomeInstalled, bool xfceInstalled, bool xrdpUsesXfce)
    {
        var ramSavings = gnomeInstalled ? 450 : 120;
        if (xfceInstalled && xrdpUsesXfce)
        {
            ramSavings += 150;
        }

        return new OptimizationGainEstimate(
            ramSavings,
            gnomeInstalled ? "Disabling GNOME session overhead should reduce background CPU churn and login latency." : "Current desktop stack is already fairly lean.",
            xrdpUsesXfce ? "XRDP is already aligned to XFCE, so improvements are mainly about trimming leftovers." : "Switching XRDP sessions to XFCE should make Rider and remote admin work feel snappier.");
    }

    private static IReadOnlyList<string> BuildLikelyIssues(
        string ubuntuVersion,
        bool xrdpInstalled,
        bool xfceInstalled,
        DesktopSessionConfiguration sessionConfiguration,
        IReadOnlyList<ServiceState> services)
    {
        var issues = new List<string>();

        if (!ubuntuVersion.StartsWith("24.", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("This module is tuned for Ubuntu 24.04-class systems. Review distro-specific differences before making destructive changes.");
        }

        if (!xrdpInstalled)
        {
            issues.Add("XRDP is not installed yet, so remote desktop access is not ready.");
        }

        if (!xfceInstalled)
        {
            issues.Add("XFCE is not installed yet, so XRDP sessions will continue to use the heavier default desktop.");
        }

        if (!sessionConfiguration.XrdpUsesXfce)
        {
            issues.Add("XRDP is not currently configured to launch XFCE by default.");
        }

        if (services.Any(item => item.Name.Contains("gdm", StringComparison.OrdinalIgnoreCase) && item.IsActive))
        {
            issues.Add("A GNOME display manager is still active. That is fine for safety, but it does keep local desktop overhead in play.");
        }

        return issues;
    }

    private static bool IsInstalled(IReadOnlyList<PackageState> packages, string packageName) =>
        packages.Any(item => item.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase) && item.IsInstalled);
}
