using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerWorkspaceViewModel(
    DesktopInspectionReport Inspection,
    string CurrentModeLabel,
    string CurrentModeSummary,
    RdpOptimizationRequestEditor Editor,
    IReadOnlyList<RdpDesktopModeOptionViewModel> Options,
    IReadOnlyList<string> Recommendations,
    RestoreSnapshot? LatestSnapshot,
    RdpOptimizationResult? LatestRun,
    IReadOnlyList<RestoreSnapshot> Snapshots,
    IReadOnlyList<RdpOptimizationResult> Runs);
