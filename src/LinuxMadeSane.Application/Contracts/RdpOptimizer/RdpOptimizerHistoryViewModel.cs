using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerHistoryViewModel(
    IReadOnlyList<RestoreSnapshot> Snapshots,
    IReadOnlyList<RdpOptimizationResult> Runs);
