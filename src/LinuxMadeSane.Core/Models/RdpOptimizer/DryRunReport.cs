// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record DryRunReport(
    Guid RunId,
    DateTimeOffset CreatedAt,
    RdpOptimizationProfile Profile,
    IReadOnlyList<string> PlannedCommands,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<OperationLogEntry> OperationLogs);
