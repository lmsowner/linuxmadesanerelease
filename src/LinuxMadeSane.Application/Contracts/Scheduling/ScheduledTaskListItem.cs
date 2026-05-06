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
