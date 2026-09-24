// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed class ScheduledTaskEditor : IValidatableObject
{
    private const string RootUserName = "root";

    public Guid? Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public ScheduledTaskKind TaskKind { get; set; } = ScheduledTaskKind.ShellCommand;

    public ScheduledTaskScheduleMode ScheduleMode { get; set; } = ScheduledTaskScheduleMode.Daily;

    public int? Minute { get; set; } = 0;

    public int? Hour { get; set; } = 3;

    public int? DayOfMonth { get; set; } = 1;

    public string DaysOfWeekCsv { get; set; } = "1";

    public string CustomCronExpression { get; set; } = string.Empty;

    [Required]
    public string RunAsUser { get; set; } = RootUserName;

    public string WorkingDirectory { get; set; } = string.Empty;

    public Guid? RunbookId { get; set; }

    public string CommandText { get; set; } = string.Empty;

    public string ScriptPath { get; set; } = string.Empty;

    public string ScriptArguments { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string DestinationPath { get; set; } = string.Empty;

    public bool CopyRecursive { get; set; } = true;

    public bool CopyPreserveAttributes { get; set; } = true;

    public bool CopyDeleteSourceAfterCopy { get; set; }

    public string MatchPatternsCsv { get; set; } = string.Empty;

    public bool MatchCaseInsensitive { get; set; }

    public ScheduledTaskAgeFilterMode AgeFilterMode { get; set; }

    public int? AgeFilterValue { get; set; }

    public ScheduledTaskAgeUnit AgeFilterUnit { get; set; } = ScheduledTaskAgeUnit.Days;

    public bool CleanupDeleteFiles { get; set; } = true;

    public bool CleanupDeleteDirectories { get; set; }

    public bool UpdatePackageLists { get; set; } = true;

    public bool UpgradeInstalledPackages { get; set; } = true;

    public bool RemoveUnusedPackages { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult("Task name is required.", [nameof(Name)]);
        }

        if (string.IsNullOrWhiteSpace(RunAsUser))
        {
            yield return new ValidationResult("Pick the Linux user that should run this task.", [nameof(RunAsUser)]);
        }

        foreach (var result in ValidateSchedule())
        {
            yield return result;
        }

        foreach (var result in ValidateTaskKind())
        {
            yield return result;
        }

        foreach (var result in ValidateSelectionFilters())
        {
            yield return result;
        }
    }

    private IEnumerable<ValidationResult> ValidateSchedule()
    {
        switch (ScheduleMode)
        {
            case ScheduledTaskScheduleMode.Hourly:
                foreach (var result in ValidateMinute(nameof(Minute)))
                {
                    yield return result;
                }

                break;
            case ScheduledTaskScheduleMode.Daily:
                foreach (var result in ValidateHourAndMinute())
                {
                    yield return result;
                }

                break;
            case ScheduledTaskScheduleMode.Weekly:
                foreach (var result in ValidateHourAndMinute())
                {
                    yield return result;
                }

                if (!TryParseWeekdays(DaysOfWeekCsv, out _))
                {
                    yield return new ValidationResult(
                        "Choose at least one weekday for a weekly task.",
                        [nameof(DaysOfWeekCsv)]);
                }

                break;
            case ScheduledTaskScheduleMode.Monthly:
                foreach (var result in ValidateHourAndMinute())
                {
                    yield return result;
                }

                if (!IsRangedInteger(DayOfMonth, 1, 31))
                {
                    yield return new ValidationResult(
                        "Monthly tasks need a day of month between 1 and 31.",
                        [nameof(DayOfMonth)]);
                }

                break;
            case ScheduledTaskScheduleMode.CustomCron:
                if (string.IsNullOrWhiteSpace(CustomCronExpression) || !LooksLikeCronExpression(CustomCronExpression))
                {
                    yield return new ValidationResult(
                        "Enter a valid cron expression or cron nickname.",
                        [nameof(CustomCronExpression)]);
                }

                break;
            case ScheduledTaskScheduleMode.Reboot:
                break;
        }
    }

    private IEnumerable<ValidationResult> ValidateTaskKind()
    {
        switch (TaskKind)
        {
            case ScheduledTaskKind.ShellCommand:
                if (string.IsNullOrWhiteSpace(CommandText))
                {
                    yield return new ValidationResult("Shell command tasks need a command.", [nameof(CommandText)]);
                }
                else if (ContainsLineBreak(CommandText))
                {
                    yield return new ValidationResult("Shell command tasks must stay on one line.", [nameof(CommandText)]);
                }

                break;
            case ScheduledTaskKind.Runbook:
                if (!RunbookId.HasValue && string.IsNullOrWhiteSpace(CommandText))
                {
                    yield return new ValidationResult("Choose a runbook for this task.", [nameof(CommandText)]);
                }

                break;
            case ScheduledTaskKind.ShellScript:
                if (string.IsNullOrWhiteSpace(ScriptPath))
                {
                    yield return new ValidationResult("Script tasks need a script path.", [nameof(ScriptPath)]);
                }

                if (ContainsLineBreak(ScriptArguments))
                {
                    yield return new ValidationResult("Script arguments must stay on one line.", [nameof(ScriptArguments)]);
                }

                break;
            case ScheduledTaskKind.FileCopy:
                if (string.IsNullOrWhiteSpace(SourcePath))
                {
                    yield return new ValidationResult("Copy tasks need a source path.", [nameof(SourcePath)]);
                }

                if (string.IsNullOrWhiteSpace(DestinationPath))
                {
                    yield return new ValidationResult("Copy tasks need a destination path.", [nameof(DestinationPath)]);
                }

                break;
            case ScheduledTaskKind.Cleanup:
                if (string.IsNullOrWhiteSpace(SourcePath))
                {
                    yield return new ValidationResult("Cleanup tasks need a target path.", [nameof(SourcePath)]);
                }

                if (SourcePath.Trim().Equals("/", StringComparison.Ordinal))
                {
                    yield return new ValidationResult("Cleanup tasks cannot target the filesystem root directly.", [nameof(SourcePath)]);
                }

                if (!CleanupDeleteFiles && !CleanupDeleteDirectories)
                {
                    yield return new ValidationResult(
                        "Pick whether cleanup should remove files, folders, or both.",
                        [nameof(CleanupDeleteFiles), nameof(CleanupDeleteDirectories)]);
                }

                if (string.IsNullOrWhiteSpace(MatchPatternsCsv) && AgeFilterMode == ScheduledTaskAgeFilterMode.Any)
                {
                    yield return new ValidationResult(
                        "Cleanup tasks need a name or extension filter, an age rule, or both.",
                        [nameof(MatchPatternsCsv), nameof(AgeFilterMode)]);
                }

                break;
            case ScheduledTaskKind.SystemUpdate:
                if (!RunAsUser.Equals(RootUserName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ValidationResult(
                        "System update tasks must run as root.",
                        [nameof(RunAsUser)]);
                }

                if (!UpdatePackageLists && !UpgradeInstalledPackages && !RemoveUnusedPackages)
                {
                    yield return new ValidationResult(
                        "Pick at least one update action.",
                        [nameof(UpdatePackageLists), nameof(UpgradeInstalledPackages), nameof(RemoveUnusedPackages)]);
                }

                break;
        }
    }

    private IEnumerable<ValidationResult> ValidateSelectionFilters()
    {
        if (AgeFilterMode == ScheduledTaskAgeFilterMode.Any)
        {
            yield break;
        }

        if (!AgeFilterValue.HasValue || AgeFilterValue.Value <= 0)
        {
            yield return new ValidationResult(
                "Enter a positive age value for the selected age rule.",
                [nameof(AgeFilterValue)]);
        }
    }

    private IEnumerable<ValidationResult> ValidateHourAndMinute()
    {
        foreach (var result in ValidateHour(nameof(Hour)))
        {
            yield return result;
        }

        foreach (var result in ValidateMinute(nameof(Minute)))
        {
            yield return result;
        }
    }

    private IEnumerable<ValidationResult> ValidateHour(string memberName)
    {
        if (!IsRangedInteger(Hour, 0, 23))
        {
            yield return new ValidationResult("Hour must be between 0 and 23.", [memberName]);
        }
    }

    private IEnumerable<ValidationResult> ValidateMinute(string memberName)
    {
        if (!IsRangedInteger(Minute, 0, 59))
        {
            yield return new ValidationResult("Minute must be between 0 and 59.", [memberName]);
        }
    }

    private static bool IsRangedInteger(int? value, int minimum, int maximum)
    {
        return value.HasValue && value.Value >= minimum && value.Value <= maximum;
    }

    private static bool TryParseWeekdays(string value, out int[] days)
    {
        days = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var day) ? day : -1)
            .Where(day => day is >= 0 and <= 6)
            .Distinct()
            .OrderBy(day => day)
            .ToArray();

        return days.Length > 0;
    }

    private static bool LooksLikeCronExpression(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith('@'))
        {
            return trimmed.Equals("@yearly", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("@annually", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("@monthly", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("@weekly", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("@daily", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("@midnight", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("@hourly", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("@reboot", StringComparison.OrdinalIgnoreCase);
        }

        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 5;
    }

    private static bool ContainsLineBreak(string value) =>
        value.Contains('\n') || value.Contains('\r');
}
