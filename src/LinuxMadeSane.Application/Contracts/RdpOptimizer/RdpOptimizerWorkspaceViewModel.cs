// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
