// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record DryRunReport(
    Guid RunId,
    DateTimeOffset CreatedAt,
    RdpOptimizationProfile Profile,
    IReadOnlyList<string> PlannedCommands,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<OperationLogEntry> OperationLogs);
