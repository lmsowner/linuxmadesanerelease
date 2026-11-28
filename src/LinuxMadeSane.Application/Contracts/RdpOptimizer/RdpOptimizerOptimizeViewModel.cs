// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerOptimizeViewModel(
    DesktopInspectionReport Inspection,
    RdpOptimizationRequestEditor Editor,
    IReadOnlyList<string> RemovableGnomePackages,
    RdpOptimizationPlan? Plan,
    RdpOptimizationResult? LastResult);
