// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record RdpOptimizationResult(
    Guid RunId,
    RdpOptimizationProfile Profile,
    bool Success,
    bool DryRun,
    bool InspectOnly,
    Guid? SnapshotId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    RdpOptimizationPlan Plan,
    DesktopInspectionReport PostInspection,
    IReadOnlyList<OperationLogEntry> OperationLogs,
    DryRunReport? DryRunReport,
    IReadOnlyList<string> Warnings);
