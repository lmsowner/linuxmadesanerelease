using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerOptimizeViewModel(
    DesktopInspectionReport Inspection,
    RdpOptimizationRequestEditor Editor,
    IReadOnlyList<string> RemovableGnomePackages,
    RdpOptimizationPlan? Plan,
    RdpOptimizationResult? LastResult);
