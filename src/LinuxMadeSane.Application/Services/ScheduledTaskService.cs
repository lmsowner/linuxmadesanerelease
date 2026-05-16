// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using LinuxMadeSane.Application.Contracts.Scheduling;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.Scheduling;

namespace LinuxMadeSane.Application.Services;

public sealed class ScheduledTaskService(
    ILinuxSchedulingModuleDataService schedulingDataService,
    ILinuxShareModuleDataService shareDataService,
    ISavedCommandStore savedCommandStore) : IScheduledTaskService
{
    public async Task<ScheduledTaskWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var tasksTask = schedulingDataService.ListTasksAsync(cancellationToken);
        var usersTask = shareDataService.ListUsersAsync(cancellationToken);
        var runbooksTask = savedCommandStore.ListByHostAsync(AiLocalMachine.ManagedHostId, cancellationToken);
        var healthTask = schedulingDataService.GetHealthAsync(cancellationToken);
        var now = DateTimeOffset.Now;

        await Task.WhenAll(tasksTask, usersTask, runbooksTask, healthTask);

        return new ScheduledTaskWorkspaceViewModel(
            tasksTask.Result
                .OrderByDescending(task => task.IsEnabled)
                .ThenByDescending(task => task.UpdatedAtUtc)
                .Select(task => MapListItem(task, now))
                .ToArray(),
            BuildUserOptions(usersTask.Result),
            BuildRunbookOptions(runbooksTask.Result),
            MapHealth(healthTask.Result));
    }

    public async Task<ScheduledTaskEditor> GetEditorAsync(Guid? taskId, CancellationToken cancellationToken = default)
    {
        if (!taskId.HasValue)
        {
            return CreateDefaultEditor();
        }

        var task = await schedulingDataService.GetTaskAsync(taskId.Value, cancellationToken);
        return task is null ? CreateDefaultEditor() : MapEditor(task);
    }

    public async Task<Guid> SaveTaskAsync(ScheduledTaskEditor editor, CancellationToken cancellationToken = default)
    {
        await HydrateRunbookTaskAsync(editor, cancellationToken);
        var compilation = ScheduledTaskCompiler.Compile(editor);
        var id = editor.Id ?? Guid.NewGuid();
        var existing = editor.Id.HasValue
            ? await schedulingDataService.GetTaskAsync(editor.Id.Value, cancellationToken)
            : null;
        var now = DateTimeOffset.UtcNow;

        var task = new ScheduledTaskDefinition(
            id,
            editor.Name.Trim(),
            editor.Description.Trim(),
            editor.IsEnabled,
            editor.TaskKind,
            editor.ScheduleMode,
            compilation.Minute,
            compilation.Hour,
            compilation.DayOfMonth,
            compilation.DaysOfWeekCsv,
            editor.CustomCronExpression.Trim(),
            compilation.CronExpression,
            compilation.ScheduleSummary,
            editor.RunAsUser.Trim(),
            editor.WorkingDirectory.Trim(),
            editor.RunbookId,
            existing?.ExecutionToken ?? GenerateExecutionToken(),
            editor.CommandText.Trim(),
            editor.ScriptPath.Trim(),
            editor.ScriptArguments.Trim(),
            editor.SourcePath.Trim(),
            editor.DestinationPath.Trim(),
            editor.CopyRecursive,
            editor.CopyPreserveAttributes,
            editor.CopyDeleteSourceAfterCopy,
            editor.MatchPatternsCsv.Trim(),
            editor.MatchCaseInsensitive,
            editor.AgeFilterMode,
            editor.AgeFilterValue ?? 0,
            editor.AgeFilterUnit,
            editor.CleanupDeleteFiles,
            editor.CleanupDeleteDirectories,
            editor.UpdatePackageLists,
            editor.UpgradeInstalledPackages,
            editor.RemoveUnusedPackages,
            compilation.CommandPreview,
            existing?.CreatedAtUtc ?? now,
            now);

        await schedulingDataService.SaveTaskAsync(task, cancellationToken);
        return id;
    }

    public Task DeleteTaskAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        schedulingDataService.DeleteTaskAsync(taskId, cancellationToken);

    public async Task<ScheduledTaskRunResultViewModel> RunTaskNowAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var result = await schedulingDataService.RunTaskNowAsync(taskId, cancellationToken);
        return new ScheduledTaskRunResultViewModel(result.Success, result.Summary);
    }

    public async Task<ScheduledTaskRunResultViewModel> TriggerTaskAsync(
        Guid taskId,
        string executionToken,
        CancellationToken cancellationToken = default)
    {
        var result = await schedulingDataService.TriggerTaskAsync(taskId, executionToken, cancellationToken);
        return new ScheduledTaskRunResultViewModel(result.Success, result.Summary);
    }

    public async Task<ScheduledTaskLogViewModel> GetTaskLogAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var log = await schedulingDataService.GetTaskLogAsync(taskId, cancellationToken);
        return new ScheduledTaskLogViewModel(
            log.TaskId,
            log.TaskName,
            log.LogFilePath,
            log.Exists,
            log.Content,
            log.LastUpdatedAtUtc,
            log.IsTruncated);
    }

    private static ScheduledTaskListItem MapListItem(ScheduledTaskDefinition task, DateTimeOffset now) =>
        new(
            task.Id,
            task.Name,
            task.Description,
            task.IsEnabled,
            task.TaskKind,
            task.ScheduleSummary,
            ScheduledTaskNextRunCalculator.DescribeNextRun(task, now),
            task.CronExpression,
            task.RunAsUser,
            task.WorkingDirectory,
            BuildListCommandPreview(task),
            ScheduledTaskPaths.GetCronFilePath(task.Id),
            ScheduledTaskPaths.GetLogFilePath(task.Id),
            task.UpdatedAtUtc);

    private static ScheduledTaskHealthViewModel MapHealth(ScheduledTaskHealthSnapshot health) =>
        new(
            health.CronDirectoryAvailable,
            health.CrontabBinaryAvailable,
            health.DetectedServiceName,
            health.ServiceState,
            health.Summary);

    private static IReadOnlyList<ScheduledTaskRunbookOption> BuildRunbookOptions(
        IReadOnlyList<LinuxMadeSane.Core.Models.SavedCommand> runbooks) =>
        runbooks
            .Where(runbook => !runbook.IsTemplate)
            .OrderByDescending(runbook => runbook.IsQuickAccess)
            .ThenBy(runbook => runbook.Name, StringComparer.OrdinalIgnoreCase)
            .Select(runbook => new ScheduledTaskRunbookOption(
                runbook.Id,
                runbook.Name,
                runbook.Description,
                runbook.CommandText,
                runbook.RequiresSudo,
                runbook.IsQuickAccess))
            .ToArray();

    private async Task HydrateRunbookTaskAsync(ScheduledTaskEditor editor, CancellationToken cancellationToken)
    {
        if (editor.TaskKind != ScheduledTaskKind.Runbook || !editor.RunbookId.HasValue)
        {
            return;
        }

        var runbook = await savedCommandStore.GetAsync(editor.RunbookId.Value, cancellationToken);
        if (runbook is null || runbook.IsTemplate || runbook.HostId != AiLocalMachine.ManagedHostId)
        {
            throw new ValidationException("Choose a valid local runnable runbook for this scheduled task.");
        }

        editor.CommandText = runbook.CommandText.Trim();
        if (runbook.RequiresSudo &&
            !editor.RunAsUser.Equals(ScheduledTaskCompiler.RootUserName, StringComparison.OrdinalIgnoreCase))
        {
            editor.RunAsUser = ScheduledTaskCompiler.RootUserName;
        }
    }

    private static IReadOnlyList<ScheduledTaskUserOption> BuildUserOptions(
        IReadOnlyList<LinuxMadeSane.Core.Models.Shares.LinuxShareUser> users)
    {
        var items = new List<ScheduledTaskUserOption>
        {
            new(
                ScheduledTaskCompiler.RootUserName,
                "root",
                "Full system task with sudo-level access",
                IsRoot: true)
        };

        items.AddRange(
            users
                .Where(user => !user.UserName.Equals(ScheduledTaskCompiler.RootUserName, StringComparison.OrdinalIgnoreCase))
                .Select(user => new ScheduledTaskUserOption(
                    user.UserName,
                    user.DisplayName.Equals(user.UserName, StringComparison.OrdinalIgnoreCase)
                        ? user.UserName
                        : $"{user.UserName} ({user.DisplayName})",
                    $"{user.PrimaryGroup} · {user.HomeDirectory}",
                    IsRoot: false)));

        return items;
    }

    private static ScheduledTaskEditor CreateDefaultEditor() =>
        new()
        {
            RunAsUser = ScheduledTaskCompiler.RootUserName
        };

    private static string GenerateExecutionToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static ScheduledTaskEditor MapEditor(ScheduledTaskDefinition task) =>
        new()
        {
            Id = task.Id,
            Name = task.Name,
            Description = task.Description,
            IsEnabled = task.IsEnabled,
            TaskKind = task.TaskKind,
            ScheduleMode = task.ScheduleMode,
            Minute = task.Minute,
            Hour = task.Hour,
            DayOfMonth = task.DayOfMonth,
            DaysOfWeekCsv = task.DaysOfWeekCsv,
            CustomCronExpression = task.CustomCronExpression,
            RunAsUser = task.RunAsUser,
            WorkingDirectory = task.WorkingDirectory,
            RunbookId = task.RunbookId,
            CommandText = task.CommandText,
            ScriptPath = task.ScriptPath,
            ScriptArguments = task.ScriptArguments,
            SourcePath = task.SourcePath,
            DestinationPath = task.DestinationPath,
            CopyRecursive = task.CopyRecursive,
            CopyPreserveAttributes = task.CopyPreserveAttributes,
            CopyDeleteSourceAfterCopy = task.CopyDeleteSourceAfterCopy,
            MatchPatternsCsv = task.MatchPatternsCsv,
            MatchCaseInsensitive = task.MatchCaseInsensitive,
            AgeFilterMode = task.AgeFilterMode,
            AgeFilterValue = task.AgeFilterValue > 0 ? task.AgeFilterValue : null,
            AgeFilterUnit = task.AgeFilterUnit,
            CleanupDeleteFiles = task.CleanupDeleteFiles,
            CleanupDeleteDirectories = task.CleanupDeleteDirectories,
            UpdatePackageLists = task.UpdatePackageLists,
            UpgradeInstalledPackages = task.UpgradeInstalledPackages,
            RemoveUnusedPackages = task.RemoveUnusedPackages
        };

    private static string BuildListCommandPreview(ScheduledTaskDefinition task)
    {
        return task.TaskKind switch
        {
            ScheduledTaskKind.ShellCommand => task.CommandText.Trim(),
            ScheduledTaskKind.Runbook => BuildRunbookListPreview(task.CommandText),
            ScheduledTaskKind.ShellScript => BuildShellScriptListPreview(task.ScriptPath, task.ScriptArguments),
            ScheduledTaskKind.FileCopy => BuildFileCopyListPreview(task),
            ScheduledTaskKind.Cleanup => BuildCleanupListPreview(task),
            ScheduledTaskKind.SystemUpdate => BuildSystemUpdateListPreview(task),
            _ => task.CommandPreview
        };
    }

    private static string BuildRunbookListPreview(string content)
    {
        var normalized = content.Trim();
        if (normalized.Length == 0)
        {
            return "Run selected runbook";
        }

        return "Run stored runbook script";
    }

    private static string BuildShellScriptListPreview(string scriptPath, string scriptArguments)
    {
        var arguments = string.IsNullOrWhiteSpace(scriptArguments)
            ? string.Empty
            : $" {scriptArguments.Trim()}";
        return $"Run {scriptPath.Trim()}{arguments}";
    }

    private static string BuildFileCopyListPreview(ScheduledTaskDefinition task)
    {
        var action = task.CopyDeleteSourceAfterCopy ? "Move" : "Copy";
        var filterSummary = BuildFilterSummary(task.MatchPatternsCsv, task.MatchCaseInsensitive, task.AgeFilterMode, task.AgeFilterValue, task.AgeFilterUnit);
        return string.IsNullOrWhiteSpace(filterSummary)
            ? $"{action} {task.SourcePath} to {task.DestinationPath}"
            : $"{action} {filterSummary} from {task.SourcePath} to {task.DestinationPath}";
    }

    private static string BuildCleanupListPreview(ScheduledTaskDefinition task)
    {
        var targetKinds = task.CleanupDeleteFiles && task.CleanupDeleteDirectories
            ? "files and folders"
            : task.CleanupDeleteDirectories
                ? "folders"
                : "files";
        var filterSummary = BuildFilterSummary(task.MatchPatternsCsv, task.MatchCaseInsensitive, task.AgeFilterMode, task.AgeFilterValue, task.AgeFilterUnit);
        return string.IsNullOrWhiteSpace(filterSummary)
            ? $"Remove {targetKinds} under {task.SourcePath}"
            : $"Remove {filterSummary} {targetKinds} under {task.SourcePath}";
    }

    private static string BuildSystemUpdateListPreview(ScheduledTaskDefinition task)
    {
        var actions = new List<string>();
        if (task.UpdatePackageLists)
        {
            actions.Add("refresh package lists");
        }

        if (task.UpgradeInstalledPackages)
        {
            actions.Add("install upgrades");
        }

        if (task.RemoveUnusedPackages)
        {
            actions.Add("remove unused packages");
        }

        return actions.Count == 0
            ? "APT maintenance"
            : string.Join(", ", actions);
    }

    private static string BuildFilterSummary(
        string matchPatternsCsv,
        bool matchCaseInsensitive,
        ScheduledTaskAgeFilterMode ageFilterMode,
        int ageFilterValue,
        ScheduledTaskAgeUnit ageFilterUnit)
    {
        var segments = new List<string>();
        var normalizedPatterns = NormalizePatterns(matchPatternsCsv);
        if (normalizedPatterns.Count > 0)
        {
            segments.Add($"matching {string.Join(", ", normalizedPatterns.Take(3))}");
        }

        if (ageFilterMode != ScheduledTaskAgeFilterMode.Any && ageFilterValue > 0)
        {
            segments.Add($"{GetAgeFilterLabel(ageFilterMode)} {ageFilterValue} {GetAgeUnitLabel(ageFilterUnit, ageFilterValue)}");
        }

        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var summary = string.Join(" and ", segments);
        return matchCaseInsensitive && normalizedPatterns.Count > 0
            ? $"{summary} (case-insensitive)"
            : summary;
    }

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
