// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskListItem(
    Guid Id,
    string Name,
    string Description,
    bool IsEnabled,
    ScheduledTaskKind TaskKind,
    string ScheduleSummary,
    string NextRunDisplay,
    string CronExpression,
    string RunAsUser,
    string WorkingDirectory,
    string CommandPreview,
    string CronFilePath,
    string LogFilePath,
    DateTimeOffset UpdatedAtUtc);
