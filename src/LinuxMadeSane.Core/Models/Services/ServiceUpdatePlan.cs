// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceUpdatePlan(
    Guid ServiceId,
    string UnitName,
    string TargetVersion,
    RepairRiskLevel RiskLevel,
    bool RequiresDaemonReload,
    bool RequiresOwnershipCorrection,
    bool RequiresDependencyRepair,
    bool RequiresConfigMerge,
    IReadOnlyList<string> AcceptedSourceTypes,
    IReadOnlyList<string> ValidationChecks,
    IReadOnlyList<string> PlannedSteps,
    IReadOnlyList<string> SuccessChecks,
    IReadOnlyList<string> RollbackTriggers,
    IReadOnlyList<string> FailureHazards);
