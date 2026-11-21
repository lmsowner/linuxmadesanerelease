// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record OptimizationGainEstimate(
    int EstimatedRamSavingsMb,
    string CpuImpactSummary,
    string UserExperienceSummary);
