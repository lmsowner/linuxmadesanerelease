namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record OptimizationGainEstimate(
    int EstimatedRamSavingsMb,
    string CpuImpactSummary,
    string UserExperienceSummary);
