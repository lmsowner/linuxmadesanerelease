using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpDesktopModeOptionViewModel(
    RdpOptimizationProfile Profile,
    string Title,
    string Summary,
    bool IsCurrent,
    bool IsReady,
    IReadOnlyList<string> MissingPackages,
    IReadOnlyList<string> Highlights);
