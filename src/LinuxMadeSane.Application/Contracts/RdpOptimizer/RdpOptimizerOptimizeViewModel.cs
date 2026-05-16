// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerOptimizeViewModel(
    DesktopInspectionReport Inspection,
    RdpOptimizationRequestEditor Editor,
    IReadOnlyList<string> RemovableGnomePackages,
    RdpOptimizationPlan? Plan,
    RdpOptimizationResult? LastResult);
