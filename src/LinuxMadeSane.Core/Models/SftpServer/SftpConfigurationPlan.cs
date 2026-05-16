// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpConfigurationPlan(
    Guid PlanId,
    string Title,
    bool IsDryRun,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SftpConfigurationAction> Actions,
    SftpValidationResult Validation,
    string? ProposedSshdConfig,
    string? BackupSummary);
