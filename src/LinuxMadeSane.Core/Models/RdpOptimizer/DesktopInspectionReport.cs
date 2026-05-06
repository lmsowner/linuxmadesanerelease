namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record DesktopInspectionReport(
    string DistributionName,
    string UbuntuVersion,
    string KernelVersion,
    string? DisplayManager,
    IReadOnlyList<string> InstalledDesktopEnvironments,
    bool IsUbuntu,
    bool XrdpInstalled,
    bool XfceInstalled,
    bool GnomeInstalled,
    bool XrdpServiceEnabled,
    bool XrdpServiceActive,
    DesktopSessionConfiguration SessionConfiguration,
    IReadOnlyList<PackageState> Packages,
    IReadOnlyList<ServiceState> Services,
    OptimizationGainEstimate LikelyGains,
    IReadOnlyList<string> LikelyIssues);
