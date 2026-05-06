namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record PackageState(
    string Name,
    bool IsInstalled,
    string Version,
    string Status);
