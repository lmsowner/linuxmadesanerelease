// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerOverviewViewModel(
    DesktopInspectionReport Inspection,
    RdpOptimizationProfile SuggestedProfile,
    RestoreSnapshot? LatestSnapshot,
    RdpOptimizationResult? LatestRun,
    IReadOnlyList<string> Recommendations);
