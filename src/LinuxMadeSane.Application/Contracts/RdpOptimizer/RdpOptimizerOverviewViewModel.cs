// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerOverviewViewModel(
    DesktopInspectionReport Inspection,
    RdpOptimizationProfile SuggestedProfile,
    RestoreSnapshot? LatestSnapshot,
    RdpOptimizationResult? LatestRun,
    IReadOnlyList<string> Recommendations);
