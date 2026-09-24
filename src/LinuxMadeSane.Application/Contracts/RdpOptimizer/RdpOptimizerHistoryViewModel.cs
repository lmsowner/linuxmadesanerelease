// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.RdpOptimizer;

public sealed record RdpOptimizerHistoryViewModel(
    IReadOnlyList<RestoreSnapshot> Snapshots,
    IReadOnlyList<RdpOptimizationResult> Runs);
