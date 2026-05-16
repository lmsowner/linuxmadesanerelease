// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using LinuxMadeSane.Application.Contracts.Scheduling;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Services;

public static class ScheduledTaskCompiler
{
    public const string RootUserName = "root";

    private static readonly string[] WeekdayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static ScheduledTaskCompilation Compile(ScheduledTaskEditor editor)
    {
        ValidateAndThrow(editor);

        var minute = GetRequiredRangedInteger(editor.Minute, 0, 59);
        var hour = GetRequiredRangedInteger(editor.Hour, 0, 23);
        var dayOfMonth = GetRequiredRangedInteger(editor.DayOfMonth, 1, 31);
        var daysOfWeek = ParseWeekdays(editor.DaysOfWeekCsv);
        var cronExpression = BuildCronExpression(editor, minute, hour, dayOfMonth, daysOfWeek);
        var scheduleSummary = BuildScheduleSummary(editor, minute, hour, dayOfMonth, daysOfWeek);
        var commandPreview = BuildCommandPreview(editor);

        return new ScheduledTaskCompilation(
            minute,
            hour,
            dayOfMonth,
            string.Join(",", daysOfWeek),
            cronExpression,
            scheduleSummary,
            commandPreview,
            BuildDisplayPreview(editor));
    }

    public static void ValidateAndThrow(ScheduledTaskEditor editor)
    {
        var context = new ValidationContext(editor);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(editor, context, results, true))
        {
            return;
        }

        throw new ValidationException(string.Join(" ", results.Select(result => result.ErrorMessage).Where(static item => !string.IsNullOrWhiteSpace(item))));
    }

    private static string BuildCronExpression(
        ScheduledTaskEditor editor,
        int minute,
        int hour,
        int dayOfMonth,
        IReadOnlyList<int> daysOfWeek) =>
        editor.ScheduleMode switch
        {
            ScheduledTaskScheduleMode.Hourly => $"{minute} * * * *",
            ScheduledTaskScheduleMode.Daily => $"{minute} {hour} * * *",
            ScheduledTaskScheduleMode.Weekly => $"{minute} {hour} * * {string.Join(",", daysOfWeek)}",
            ScheduledTaskScheduleMode.Monthly => $"{minute} {hour} {dayOfMonth} * *",
            ScheduledTaskScheduleMode.CustomCron => editor.CustomCronExpression.Trim(),
            ScheduledTaskScheduleMode.Reboot => "@reboot",
            _ => throw new InvalidOperationException($"Unsupported schedule mode '{editor.ScheduleMode}'.")
        };

    private static string BuildScheduleSummary(
        ScheduledTaskEditor editor,
        int minute,
        int hour,
        int dayOfMonth,
        IReadOnlyList<int> daysOfWeek)
    {
        var time = FormatTime(hour, minute);
        return editor.ScheduleMode switch
        {
            ScheduledTaskScheduleMode.Hourly => $"Every hour at :{minute:00}",
            ScheduledTaskScheduleMode.Daily => $"Every day at {time}",
            ScheduledTaskScheduleMode.Weekly => $"Every {string.Join(", ", daysOfWeek.Select(day => WeekdayNames[day]))} at {time}",
            ScheduledTaskScheduleMode.Monthly => $"Day {dayOfMonth} of every month at {time}",
            ScheduledTaskScheduleMode.CustomCron => $"Custom cron: {editor.CustomCronExpression.Trim()}",
            ScheduledTaskScheduleMode.Reboot => "At system startup",
            _ => "Custom schedule"
        };
    }

    private static string BuildCommandPreview(ScheduledTaskEditor editor)
    {
        var innerCommand = editor.TaskKind switch
        {
            ScheduledTaskKind.ShellCommand => editor.CommandText.Trim(),
            ScheduledTaskKind.Runbook => RunbookExecutionCommandBuilder.BuildSchedulerCommand(editor.CommandText),
            ScheduledTaskKind.ShellScript => BuildShellScriptCommand(editor),
            ScheduledTaskKind.FileCopy => BuildFileCopyCommand(editor),
            ScheduledTaskKind.Cleanup => BuildCleanupCommand(editor),
            ScheduledTaskKind.SystemUpdate => BuildSystemUpdateCommand(editor),
            _ => throw new InvalidOperationException($"Unsupported task kind '{editor.TaskKind}'.")
        };

        return string.IsNullOrWhiteSpace(editor.WorkingDirectory)
            ? innerCommand
            : $"cd {Quote(editor.WorkingDirectory.Trim())} && {innerCommand}";
    }

    private static string BuildShellScriptCommand(ScheduledTaskEditor editor)
    {
        var argumentSuffix = string.IsNullOrWhiteSpace(editor.ScriptArguments)
            ? string.Empty
            : $" {editor.ScriptArguments.Trim()}";

        return $"bash {Quote(editor.ScriptPath.Trim())}{argumentSuffix}";
    }

    private static string BuildFileCopyCommand(ScheduledTaskEditor editor)
    {
        var source = editor.SourcePath.Trim();
        var destination = editor.DestinationPath.Trim();
        if (HasSelectionFilters(editor))
        {
            return RunbookExecutionCommandBuilder.BuildScheduledShellScriptCommand(
                BuildFilteredFileCopyScript(editor),
                "lms-schedule-copy");
        }

        var destinationParent = ResolveDestinationParent(destination);
        var ensureParent = string.IsNullOrWhiteSpace(destinationParent)
            ? string.Empty
            : $"mkdir -p {Quote(destinationParent)} && ";
        if (editor.CopyDeleteSourceAfterCopy)
        {
            return $"{ensureParent}mv -- {Quote(source)} {Quote(destination)}";
        }

        var copyCommand = BuildCopyCommand(editor.CopyPreserveAttributes, editor.CopyRecursive);
        return $"{ensureParent}{copyCommand} -- {Quote(source)} {Quote(destination)}";
    }

    private static string BuildCleanupCommand(ScheduledTaskEditor editor) =>
        RunbookExecutionCommandBuilder.BuildScheduledShellScriptCommand(
            BuildCleanupScript(editor),
            "lms-schedule-cleanup");

    private static string BuildSystemUpdateCommand(ScheduledTaskEditor editor)
    {
        var segments = new List<string>();
        if (editor.UpdatePackageLists)
        {
            segments.Add("apt-get update");
        }

        if (editor.UpgradeInstalledPackages)
        {
            segments.Add("DEBIAN_FRONTEND=noninteractive apt-get -y upgrade");
        }

        if (editor.RemoveUnusedPackages)
        {
            segments.Add("DEBIAN_FRONTEND=noninteractive apt-get -y autoremove");
        }

        return string.Join(" && ", segments);
    }

    private static string ResolveDestinationParent(string destination)
    {
        var normalized = destination.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return Path.GetDirectoryName(normalized) ?? string.Empty;
    }

    private static string FormatTime(int hour, int minute) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{hour:00}:{minute:00}");

    private static int GetRequiredRangedInteger(int? value, int minimum, int maximum)
    {
        if (value.HasValue &&
            value.Value >= minimum &&
            value.Value <= maximum)
        {
            return value.Value;
        }

        return minimum;
    }

    private static IReadOnlyList<int> ParseWeekdays(string value)
    {
        var days = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1)
            .Where(day => day is >= 0 and <= 6)
            .Distinct()
            .OrderBy(day => day)
            .ToArray();

        return days.Length == 0 ? [1] : days;
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static string BuildDisplayPreview(ScheduledTaskEditor editor) =>
        editor.TaskKind switch
        {
            ScheduledTaskKind.ShellCommand => editor.CommandText.Trim(),
            ScheduledTaskKind.Runbook => BuildRunbookDisplayPreview(editor),
            ScheduledTaskKind.ShellScript => BuildShellScriptDisplayPreview(editor),
            ScheduledTaskKind.FileCopy => BuildFileCopyDisplayPreview(editor),
            ScheduledTaskKind.Cleanup => BuildCleanupDisplayPreview(editor),
            ScheduledTaskKind.SystemUpdate => BuildSystemUpdateDisplayPreview(editor),
            _ => "Task"
        };

    private static string BuildRunbookDisplayPreview(ScheduledTaskEditor editor)
    {
        var normalized = editor.CommandText.Trim();
        if (normalized.Length == 0)
        {
            return "Run selected runbook";
        }

        return "Run stored runbook script";
    }

    private static string BuildShellScriptDisplayPreview(ScheduledTaskEditor editor)
    {
        var arguments = string.IsNullOrWhiteSpace(editor.ScriptArguments)
            ? string.Empty
            : $" {editor.ScriptArguments.Trim()}";
        return $"Run {editor.ScriptPath.Trim()}{arguments}";
    }

    private static string BuildFileCopyDisplayPreview(ScheduledTaskEditor editor)
    {
        var action = editor.CopyDeleteSourceAfterCopy ? "Move" : "Copy";
        var filterSummary = BuildSelectionFilterSummary(editor);
        return string.IsNullOrWhiteSpace(filterSummary)
            ? $"{action} {editor.SourcePath.Trim()} to {editor.DestinationPath.Trim()}"
            : $"{action} {filterSummary} from {editor.SourcePath.Trim()} to {editor.DestinationPath.Trim()}";
    }

    private static string BuildCleanupDisplayPreview(ScheduledTaskEditor editor)
    {
        var targetKinds = editor.CleanupDeleteFiles && editor.CleanupDeleteDirectories
            ? "files and folders"
            : editor.CleanupDeleteDirectories
                ? "folders"
                : "files";
        var filterSummary = BuildSelectionFilterSummary(editor);
        return string.IsNullOrWhiteSpace(filterSummary)
            ? $"Remove {targetKinds} under {editor.SourcePath.Trim()}"
            : $"Remove {filterSummary} {targetKinds} under {editor.SourcePath.Trim()}";
    }

    private static string BuildSystemUpdateDisplayPreview(ScheduledTaskEditor editor)
    {
        var actions = new List<string>();
        if (editor.UpdatePackageLists)
        {
            actions.Add("refresh package lists");
        }

        if (editor.UpgradeInstalledPackages)
        {
            actions.Add("install upgrades");
        }

        if (editor.RemoveUnusedPackages)
        {
            actions.Add("remove unused packages");
        }

        return actions.Count == 0
            ? "APT maintenance"
            : string.Join(", ", actions);
    }

    private static string BuildSelectionFilterSummary(ScheduledTaskEditor editor)
    {
        var segments = new List<string>();
        var normalizedPatterns = NormalizePatterns(editor.MatchPatternsCsv);
        if (normalizedPatterns.Count > 0)
        {
            segments.Add($"matching {string.Join(", ", normalizedPatterns.Take(3))}");
        }

        var ageValue = editor.AgeFilterValue.GetValueOrDefault();
        if (editor.AgeFilterMode != ScheduledTaskAgeFilterMode.Any && ageValue > 0)
        {
            segments.Add($"{GetAgeFilterLabel(editor.AgeFilterMode)} {ageValue} {GetAgeUnitLabel(editor.AgeFilterUnit, ageValue)}");
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var summary = string.Join(" and ", segments);
        return editor.MatchCaseInsensitive && normalizedPatterns.Count > 0
            ? $"{summary} (case-insensitive)"
            : summary;
    }

    private static string BuildFilteredFileCopyScript(ScheduledTaskEditor editor)
    {
        var builder = new StringBuilder();
        AppendScriptPreamble(builder);
        builder.AppendLine($"source_root={Quote(editor.SourcePath.Trim())}");
        builder.AppendLine($"destination_root={Quote(editor.DestinationPath.Trim())}");
        builder.AppendLine($"move_after_copy={(editor.CopyDeleteSourceAfterCopy ? "1" : "0")}");
        builder.AppendLine($"include_subfolders={(editor.CopyRecursive ? "1" : "0")}");
        builder.AppendLine($"copy_command={Quote(BuildCopyCommand(editor.CopyPreserveAttributes, editor.CopyRecursive))}");
        AppendSharedSelectionFilterConfiguration(builder, editor);
        AppendSharedSelectionFilterFunctions(builder);
        builder.AppendLine("""
copy_entry() {
    local source_path="$1"
    local relative_path="$2"
    local target_path="$destination_root/$relative_path"
    mkdir -p "$(dirname "$target_path")"
    eval "$copy_command -- $(printf '%q' "$source_path") $(printf '%q' "$target_path")"
    if [ "$move_after_copy" = "1" ]; then
        rm -f -- "$source_path"
    fi
}

if [ -f "$source_root" ] || [ -L "$source_root" ]; then
    if matches_name "$(basename "$source_root")" && matches_age "$source_root"; then
        mkdir -p "$(dirname "$destination_root")"
        if [ "$move_after_copy" = "1" ]; then
            mv -- "$source_root" "$destination_root"
        else
            eval "$copy_command -- $(printf '%q' "$source_root") $(printf '%q' "$destination_root")"
        fi
    fi
elif [ -d "$source_root" ]; then
    find_args=("$source_root" -mindepth 1)
    if [ "$include_subfolders" != "1" ]; then
        find_args+=(-maxdepth 1)
    fi
    find_args+=(-type f)

    while IFS= read -r -d '' source_path; do
        if ! matches_name "$(basename "$source_path")"; then
            continue
        fi
        if ! matches_age "$source_path"; then
            continue
        fi

        relative_path="${source_path#"$source_root"/}"
        copy_entry "$source_path" "$relative_path"
    done < <(find "${find_args[@]}" -print0)

    if [ "$move_after_copy" = "1" ]; then
        find "$source_root" -depth -type d -empty -delete
    fi
else
    printf 'Source path not found: %s\n' "$source_root" >&2
    exit 1
fi
""");
        return builder.ToString();
    }

    private static string BuildCleanupScript(ScheduledTaskEditor editor)
    {
        var builder = new StringBuilder();
        AppendScriptPreamble(builder);
        builder.AppendLine($"cleanup_root={Quote(editor.SourcePath.Trim())}");
        builder.AppendLine($"include_subfolders={(editor.CopyRecursive ? "1" : "0")}");
        builder.AppendLine($"remove_files={(editor.CleanupDeleteFiles ? "1" : "0")}");
        builder.AppendLine($"remove_directories={(editor.CleanupDeleteDirectories ? "1" : "0")}");
        AppendSharedSelectionFilterConfiguration(builder, editor);
        AppendSharedSelectionFilterFunctions(builder);
        builder.AppendLine("""
delete_entry() {
    local entry_path="$1"
    if [ -d "$entry_path" ] && [ ! -L "$entry_path" ]; then
        rm -rf -- "$entry_path"
    else
        rm -f -- "$entry_path"
    fi
}

if [ -f "$cleanup_root" ] || [ -L "$cleanup_root" ]; then
    if [ "$remove_files" = "1" ] && matches_name "$(basename "$cleanup_root")" && matches_age "$cleanup_root"; then
        delete_entry "$cleanup_root"
    fi
elif [ -d "$cleanup_root" ]; then
    find_args=("$cleanup_root" -mindepth 1 -depth)
    if [ "$include_subfolders" != "1" ]; then
        find_args+=(-maxdepth 1)
    fi

    if [ "$remove_files" = "1" ] && [ "$remove_directories" = "1" ]; then
        find_args+=( \( -type f -o -type d -o -type l \) )
    elif [ "$remove_files" = "1" ]; then
        find_args+=( \( -type f -o -type l \) )
    else
        find_args+=( -type d )
    fi

    while IFS= read -r -d '' entry_path; do
        if ! matches_name "$(basename "$entry_path")"; then
            continue
        fi
        if ! matches_age "$entry_path"; then
            continue
        fi

        delete_entry "$entry_path"
    done < <(find "${find_args[@]}" -print0)
else
    printf 'Cleanup path not found: %s\n' "$cleanup_root" >&2
    exit 1
fi
""");
        return builder.ToString();
    }

    private static void AppendScriptPreamble(StringBuilder builder)
    {
        builder.AppendLine("#!/usr/bin/env bash");
        builder.AppendLine("set -euo pipefail");
        builder.AppendLine();
    }

    private static void AppendSharedSelectionFilterConfiguration(StringBuilder builder, ScheduledTaskEditor editor)
    {
        builder.AppendLine($"case_insensitive={(editor.MatchCaseInsensitive ? "1" : "0")}");
        builder.AppendLine($"age_mode={Quote(GetAgeFilterToken(editor.AgeFilterMode))}");
        builder.AppendLine($"age_seconds={GetAgeFilterSeconds(editor)}");
        builder.AppendLine("patterns=(");
        foreach (var pattern in NormalizePatterns(editor.MatchPatternsCsv))
        {
            builder.Append("    ").Append(Quote(pattern)).AppendLine();
        }
        builder.AppendLine(")");
        builder.AppendLine();
    }

    private static void AppendSharedSelectionFilterFunctions(StringBuilder builder)
    {
        builder.AppendLine("""
matches_name() {
    local candidate="$1"
    if [ "${#patterns[@]}" -eq 0 ]; then
        return 0
    fi

    if [ "$case_insensitive" = "1" ]; then
        candidate="${candidate,,}"
    fi

    local pattern
    local comparison
    for pattern in "${patterns[@]}"; do
        comparison="$pattern"
        if [ "$case_insensitive" = "1" ]; then
            comparison="${comparison,,}"
        fi
        case "$candidate" in
            $comparison) return 0 ;;
        esac
    done

    return 1
}

matches_age() {
    local entry_path="$1"
    if [ "$age_mode" = "any" ] || [ "$age_seconds" -le 0 ]; then
        return 0
    fi

    local modified_epoch
    modified_epoch=$(stat -c %Y "$entry_path")
    local now_epoch
    now_epoch=$(date +%s)
    local entry_age
    entry_age=$((now_epoch - modified_epoch))

    if [ "$age_mode" = "older" ]; then
        [ "$entry_age" -ge "$age_seconds" ]
    else
        [ "$entry_age" -le "$age_seconds" ]
    fi
}
""");
    }

    private static bool HasSelectionFilters(ScheduledTaskEditor editor) =>
        NormalizePatterns(editor.MatchPatternsCsv).Count > 0 ||
        editor.AgeFilterMode != ScheduledTaskAgeFilterMode.Any;

    private static IReadOnlyList<string> NormalizePatterns(string value) =>
        value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static pattern => pattern.Trim())
            .Where(static pattern => pattern.Length > 0)
            .Select(static pattern => pattern.StartsWith(".", StringComparison.Ordinal) && !pattern.Contains('*') && !pattern.Contains('?')
                ? $"*{pattern}"
                : pattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildCopyCommand(bool preserveAttributes, bool includeSubfolders) =>
        preserveAttributes
            ? "cp -a"
            : includeSubfolders
                ? "cp -r"
                : "cp";

    private static string GetAgeFilterToken(ScheduledTaskAgeFilterMode mode) => mode switch
    {
        ScheduledTaskAgeFilterMode.OlderThan => "older",
        ScheduledTaskAgeFilterMode.NewerThan => "newer",
        _ => "any"
    };

    private static long GetAgeFilterSeconds(ScheduledTaskEditor editor)
    {
        if (editor.AgeFilterMode == ScheduledTaskAgeFilterMode.Any || !editor.AgeFilterValue.HasValue || editor.AgeFilterValue.Value <= 0)
        {
            return 0;
        }

        return editor.AgeFilterUnit switch
        {
            ScheduledTaskAgeUnit.Minutes => editor.AgeFilterValue.Value * 60L,
            ScheduledTaskAgeUnit.Hours => editor.AgeFilterValue.Value * 60L * 60L,
            ScheduledTaskAgeUnit.Days => editor.AgeFilterValue.Value * 60L * 60L * 24L,
            ScheduledTaskAgeUnit.Weeks => editor.AgeFilterValue.Value * 60L * 60L * 24L * 7L,
            _ => 0L
        };
    }

    private static string GetAgeFilterLabel(ScheduledTaskAgeFilterMode mode) => mode switch
    {
        ScheduledTaskAgeFilterMode.OlderThan => "older than",
        ScheduledTaskAgeFilterMode.NewerThan => "newer than",
        _ => string.Empty
    };

    private static string GetAgeUnitLabel(ScheduledTaskAgeUnit unit, int value)
    {
        var plural = value == 1 ? string.Empty : "s";
        return unit switch
        {
            ScheduledTaskAgeUnit.Minutes => $"minute{plural}",
            ScheduledTaskAgeUnit.Hours => $"hour{plural}",
            ScheduledTaskAgeUnit.Days => $"day{plural}",
            ScheduledTaskAgeUnit.Weeks => $"week{plural}",
            _ => $"day{plural}"
        };
    }
}

public sealed record ScheduledTaskCompilation(
    int Minute,
    int Hour,
    int DayOfMonth,
    string DaysOfWeekCsv,
    string CronExpression,
    string ScheduleSummary,
    string CommandPreview,
    string DisplayPreview);
