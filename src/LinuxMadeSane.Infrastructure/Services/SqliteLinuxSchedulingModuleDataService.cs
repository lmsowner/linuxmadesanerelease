using System.Security.Cryptography;
using System.Text;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Scheduling;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteLinuxSchedulingModuleDataService(
    LinuxMadeSaneDbContext dbContext,
    ILinuxCommandRunner commandRunner,
    ISavedCommandStore savedCommandStore,
    IConfiguration configuration) : ILinuxSchedulingModuleDataService
{
    private const int TaskLogLineLimit = 400;
    private readonly string schedulerCallbackBaseUrl = ResolveSchedulerCallbackBaseUrl(configuration);

    public async Task<IReadOnlyList<ScheduledTaskDefinition>> ListTasksAsync(CancellationToken cancellationToken = default) =>
        (await dbContext.ScheduledTasks
                .AsNoTracking()
                .Select(task => Map(task))
                .ToArrayAsync(cancellationToken))
            .OrderByDescending(task => task.UpdatedAtUtc)
            .ThenBy(task => task.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<ScheduledTaskDefinition?> GetTaskAsync(Guid id, CancellationToken cancellationToken = default) =>
        Map(await dbContext.ScheduledTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(task => task.Id == id, cancellationToken));

    public async Task SaveTaskAsync(ScheduledTaskDefinition task, CancellationToken cancellationToken = default)
    {
        if (task.IsEnabled)
        {
            await EnsureLogDirectoryAsync(cancellationToken);
            await InstallCronFileAsync(task, cancellationToken);
        }
        else
        {
            await RemoveCronFileAsync(task.Id, cancellationToken);
        }

        var entity = await dbContext.ScheduledTasks.SingleOrDefaultAsync(item => item.Id == task.Id, cancellationToken);
        if (entity is null)
        {
            dbContext.ScheduledTasks.Add(Map(task));
        }
        else
        {
            Apply(entity, task);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTaskAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RemoveCronFileAsync(id, cancellationToken);

        var entity = await dbContext.ScheduledTasks.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        dbContext.ScheduledTasks.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ScheduledTaskRunResult> RunTaskNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await GetTaskAsync(id, cancellationToken);
        if (task is null)
        {
            throw new InvalidOperationException("Scheduled task not found.");
        }

        return await ExecuteTaskAsync(task, cancellationToken);
    }

    public async Task<ScheduledTaskRunResult> TriggerTaskAsync(
        Guid id,
        string executionToken,
        CancellationToken cancellationToken = default)
    {
        var task = await GetTaskAsync(id, cancellationToken);
        if (task is null || !IsValidExecutionToken(task.ExecutionToken, executionToken))
        {
            return new ScheduledTaskRunResult(false, "Scheduled task trigger rejected.");
        }

        return await ExecuteTaskAsync(task, cancellationToken);
    }

    private async Task<ScheduledTaskRunResult> ExecuteTaskAsync(
        ScheduledTaskDefinition task,
        CancellationToken cancellationToken)
    {
        await EnsureLogDirectoryAsync(cancellationToken);
        var executionCommand = await ResolveExecutionCommandAsync(task, cancellationToken);
        if (string.IsNullOrWhiteSpace(executionCommand))
        {
            return new ScheduledTaskRunResult(false, $"'{task.Name}' does not have a runnable command.");
        }

        var logPath = ScheduledTaskPaths.GetLogFilePath(task.Id);
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", BuildRunNowCommand(task, executionCommand, logPath)],
                RequiresSudo: true,
                Timeout: TimeSpan.FromMinutes(30),
                Description: $"Run scheduled task '{task.Name}' now"),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return new ScheduledTaskRunResult(
                true,
                $"Ran '{task.Name}'. Output was appended to {logPath}.");
        }

        var detail = FirstNonEmptyLine(result.StandardError, result.StandardOutput)
            ?? $"exit code {result.ExitCode}";
        return new ScheduledTaskRunResult(
            false,
            $"Run now failed for '{task.Name}': {detail}");
    }

    public async Task<ScheduledTaskLogSnapshot> GetTaskLogAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var task = await GetTaskAsync(id, cancellationToken);
        if (task is null)
        {
            throw new InvalidOperationException("Scheduled task not found.");
        }

        var logPath = ScheduledTaskPaths.GetLogFilePath(task.Id);
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", BuildTaskLogInspectionScript(logPath, TaskLogLineLimit)],
                RequiresSudo: true,
                Timeout: TimeSpan.FromSeconds(12),
                Description: $"Read scheduled task log for '{task.Name}'"),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var detail = FirstNonEmptyLine(result.StandardError, result.StandardOutput)
                ?? $"exit code {result.ExitCode}";
            throw new InvalidOperationException($"Reading scheduled task log for '{task.Name}' failed: {detail}");
        }

        var (metadata, content) = ParseTaskLogOutput(result.StandardOutput);
        var exists = metadata.TryGetValue("exists", out var existsText) &&
            existsText.Equals("true", StringComparison.OrdinalIgnoreCase);
        var lineCount = metadata.TryGetValue("line_count", out var lineCountText) &&
            int.TryParse(lineCountText, out var parsedLineCount)
                ? parsedLineCount
                : 0;
        DateTimeOffset? lastUpdatedAtUtc = metadata.TryGetValue("modified_epoch", out var modifiedEpochText) &&
            long.TryParse(modifiedEpochText, out var modifiedEpoch)
                ? DateTimeOffset.FromUnixTimeSeconds(modifiedEpoch)
                : null;

        return new ScheduledTaskLogSnapshot(
            task.Id,
            task.Name,
            logPath,
            exists,
            content,
            lastUpdatedAtUtc,
            lineCount > TaskLogLineLimit);
    }

    public async Task<ScheduledTaskHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var cronDirectoryAvailable = Directory.Exists(ScheduledTaskPaths.CronDirectoryPath);
        var crontabBinaryAvailable =
            File.Exists("/usr/bin/crontab") ||
            File.Exists("/bin/crontab") ||
            File.Exists("/usr/sbin/crontab") ||
            File.Exists("/sbin/crontab");

        var inspection = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "bash",
                ["-lc", BuildHealthInspectionScript()],
                RequiresSudo: false,
                Timeout: TimeSpan.FromSeconds(8),
                Description: "Inspect cron scheduler state")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        var values = ParseKeyValueLines(inspection.StandardOutput);
        var serviceName = values.TryGetValue("service", out var parsedServiceName)
            ? parsedServiceName
            : string.Empty;
        var serviceState = values.TryGetValue("state", out var parsedServiceState)
            ? parsedServiceState
            : string.Empty;
        var summary = BuildHealthSummary(cronDirectoryAvailable, crontabBinaryAvailable, serviceName, serviceState);

        return new ScheduledTaskHealthSnapshot(
            cronDirectoryAvailable,
            crontabBinaryAvailable,
            serviceName,
            serviceState,
            summary);
    }

    private async Task EnsureLogDirectoryAsync(CancellationToken cancellationToken)
    {
        await RunRequiredCommandAsync(
            "mkdir",
            ["-p", ScheduledTaskPaths.LogDirectoryPath],
            $"Prepare scheduled task log directory {ScheduledTaskPaths.LogDirectoryPath}",
            requiresSudo: true,
            cancellationToken);
    }

    private async Task InstallCronFileAsync(ScheduledTaskDefinition task, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"linuxmadesane-schedule-{task.Id:N}.cron");

        try
        {
            await File.WriteAllTextAsync(tempPath, BuildCronFileContents(task), cancellationToken);
            await RunRequiredCommandAsync(
                "install",
                ["-m", "0600", tempPath, ScheduledTaskPaths.GetCronFilePath(task.Id)],
                $"Install cron schedule for {task.Name}",
                requiresSudo: true,
                cancellationToken);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task RemoveCronFileAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await RunRequiredCommandAsync(
            "rm",
            ["-f", ScheduledTaskPaths.GetCronFilePath(taskId)],
            $"Remove cron schedule {taskId:N}",
            requiresSudo: true,
            cancellationToken);
    }

    private static string BuildRunNowCommand(
        ScheduledTaskDefinition task,
        string executionCommand,
        string logPath)
    {
        var quotedLogPath = Quote(logPath);
        var quotedExecutionCommand = Quote(executionCommand);
        var runCommand = RequiresRootExecution(task.RunAsUser)
            ? $"bash -lc {quotedExecutionCommand}"
            : $"sudo -n -u {Quote(task.RunAsUser.Trim())} bash -lc {quotedExecutionCommand}";

        return RequiresRootExecution(task.RunAsUser)
            ? $"printf '\\n=== [LMS task start] {SanitizeLogLabel(task.Name)} %s ===\\n' \"$(date -Is)\" >> {quotedLogPath}; {runCommand} >> {quotedLogPath} 2>&1; lms_task_rc=$?; printf '=== [LMS task end] {SanitizeLogLabel(task.Name)} %s rc=%s ===\\n' \"$(date -Is)\" \"$lms_task_rc\" >> {quotedLogPath}; exit \"$lms_task_rc\""
            : $"printf '\\n=== [LMS task start] {SanitizeLogLabel(task.Name)} %s ===\\n' \"$(date -Is)\" >> {quotedLogPath}; {runCommand} >> {quotedLogPath} 2>&1; lms_task_rc=$?; printf '=== [LMS task end] {SanitizeLogLabel(task.Name)} %s rc=%s ===\\n' \"$(date -Is)\" \"$lms_task_rc\" >> {quotedLogPath}; exit \"$lms_task_rc\"";
    }

    private string BuildCronFileContents(ScheduledTaskDefinition task)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Managed by Linux Made Sane");
        builder.AppendLine($"# Task: {SanitizeComment(task.Name)}");
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            builder.AppendLine($"# {SanitizeComment(task.Description)}");
        }

        builder.AppendLine("SHELL=/bin/bash");
        builder.AppendLine("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
        builder.AppendLine("MAILTO=\"\"");
        builder.Append(task.CronExpression);
        builder.Append(' ');
        builder.Append(task.RunAsUser);
        builder.Append(' ');
        builder.Append(EscapeCronCommand(BuildCronTriggerCommand(task)));
        builder.Append(" >> ");
        builder.Append(Quote(ScheduledTaskPaths.GetLogFilePath(task.Id)));
        builder.AppendLine(" 2>&1");

        return builder.ToString();
    }

    private async Task RunRequiredCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool requiresSudo,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, requiresSudo, TimeSpan.FromSeconds(20), description),
            dryRun: false,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = FirstNonEmptyLine(result.StandardError, result.StandardOutput)
            ?? $"exit code {result.ExitCode}";
        throw new InvalidOperationException($"{description} failed: {detail}");
    }

    private async Task<string> ResolveExecutionCommandAsync(
        ScheduledTaskDefinition task,
        CancellationToken cancellationToken)
    {
        if (task.TaskKind != ScheduledTaskKind.Runbook)
        {
            return task.CommandPreview.Trim();
        }

        if (!task.RunbookId.HasValue)
        {
            return task.CommandPreview.Trim();
        }

        var runbook = await savedCommandStore.GetAsync(task.RunbookId.Value, cancellationToken);
        if (runbook is null || runbook.IsTemplate)
        {
            throw new InvalidOperationException($"The saved runbook for '{task.Name}' no longer exists or is not runnable.");
        }

        var command = RunbookExecutionCommandBuilder.BuildSchedulerCommand(runbook.CommandText);
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(task.WorkingDirectory)
            ? command
            : $"cd {Quote(task.WorkingDirectory.Trim())} && {command}";
    }

    private string BuildCronTriggerCommand(ScheduledTaskDefinition task)
    {
        var targetUrl = $"{schedulerCallbackBaseUrl.TrimEnd('/')}{ScheduledTaskTrigger.GetPath(task.Id)}";
        var headerValue = $"{ScheduledTaskTrigger.HeaderName}: {task.ExecutionToken}";
        if (schedulerCallbackBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return $"curl -k -fsS --max-time 1800 -X POST -H {Quote(headerValue)} {Quote(targetUrl)}";
        }

        return $"curl -fsS --max-time 1800 -X POST -H {Quote(headerValue)} {Quote(targetUrl)}";
    }

    private static bool IsValidExecutionToken(string expected, string provided)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected.Trim());
        var providedBytes = Encoding.UTF8.GetBytes(provided.Trim());
        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static string BuildHealthInspectionScript() =>
        """
        service_name=""
        service_state=""
        if command -v systemctl >/dev/null 2>&1; then
          if systemctl list-unit-files cron.service >/dev/null 2>&1; then
            service_name="cron"
            service_state="$(systemctl is-active cron 2>/dev/null || true)"
          elif systemctl list-unit-files crond.service >/dev/null 2>&1; then
            service_name="crond"
            service_state="$(systemctl is-active crond 2>/dev/null || true)"
          fi
        fi
        printf 'service\t%s\n' "$service_name"
        printf 'state\t%s\n' "$service_state"
        """;

    private static string BuildTaskLogInspectionScript(string logPath, int maxLines) =>
        $"""
        log_path={Quote(logPath)}
        if [ ! -f "$log_path" ]; then
          printf 'exists\tfalse\n'
          printf '__LOG__\n'
          exit 0
        fi
        line_count=$(wc -l < "$log_path" 2>/dev/null || echo 0)
        modified_epoch=$(stat -c %Y "$log_path" 2>/dev/null || echo "")
        printf 'exists\ttrue\n'
        printf 'line_count\t%s\n' "$line_count"
        printf 'modified_epoch\t%s\n' "$modified_epoch"
        printf '__LOG__\n'
        tail -n {maxLines} "$log_path" 2>/dev/null || true
        """;

    private static Dictionary<string, string> ParseKeyValueLines(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = line.IndexOf('\t');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            values[line[..separatorIndex]] = line[(separatorIndex + 1)..].Trim();
        }

        return values;
    }

    private static (Dictionary<string, string> Metadata, string Content) ParseTaskLogOutput(string output)
    {
        const string marker = "__LOG__\n";
        var normalizedOutput = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var markerIndex = normalizedOutput.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            return (
                ParseKeyValueLines(normalizedOutput[..markerIndex]),
                normalizedOutput[(markerIndex + marker.Length)..].TrimEnd('\n'));
        }

        if (normalizedOutput.EndsWith("__LOG__", StringComparison.Ordinal))
        {
            return (
                ParseKeyValueLines(normalizedOutput[..^"__LOG__".Length]),
                string.Empty);
        }

        return (ParseKeyValueLines(normalizedOutput), string.Empty);
    }

    private static string BuildHealthSummary(
        bool cronDirectoryAvailable,
        bool crontabBinaryAvailable,
        string serviceName,
        string serviceState)
    {
        if (!cronDirectoryAvailable)
        {
            return "Linux Made Sane could not find /etc/cron.d on this box.";
        }

        if (!string.IsNullOrWhiteSpace(serviceName) &&
            serviceState.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return $"{serviceName} is running. LMS can install managed scheduled tasks now.";
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var readableState = string.IsNullOrWhiteSpace(serviceState) ? "unknown" : serviceState;
            return $"{serviceName} is {readableState}. LMS can write managed cron files, but the scheduler may need starting.";
        }

        return crontabBinaryAvailable
            ? "Cron tooling is present, but LMS could not confirm the scheduler service state."
            : "LMS could not find cron tooling on this box.";
    }

    private static bool RequiresRootExecution(string runAsUser) =>
        string.IsNullOrWhiteSpace(runAsUser) ||
        runAsUser.Trim().Equals("root", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeComment(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string SanitizeLogLabel(string value) =>
        SanitizeComment(value)
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal);

    private static string Quote(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";

    private static string EscapeCronCommand(string value) =>
        value.Replace("%", "\\%", StringComparison.Ordinal);

    private static string ResolveSchedulerCallbackBaseUrl(IConfiguration configuration)
    {
        var configuredUrls =
            configuration["URLS"] ??
            configuration["ASPNETCORE_URLS"] ??
            configuration["DOTNET_URLS"];

        var candidates = string.IsNullOrWhiteSpace(configuredUrls)
            ? configuration.GetSection("Server:Urls").Get<string[]>() ?? []
            : configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var selected = candidates
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(static uri => uri is not null)
            .OrderBy(uri => uri!.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

        if (selected is null)
        {
            return "http://127.0.0.1:5080";
        }

        var builder = new UriBuilder(selected)
        {
            Host = "127.0.0.1"
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    private static string? FirstNonEmptyLine(params string[] values) =>
        values
            .SelectMany(value => value.Split('\n'))
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static ScheduledTaskDefinition Map(ScheduledTaskEntity? entity) =>
        entity is null
            ? null!
            : new ScheduledTaskDefinition(
                entity.Id,
                entity.Name,
                entity.Description,
                entity.IsEnabled,
                (ScheduledTaskKind)entity.TaskKind,
                (ScheduledTaskScheduleMode)entity.ScheduleMode,
                entity.Minute,
                entity.Hour,
                entity.DayOfMonth,
                entity.DaysOfWeekCsv,
                entity.CustomCronExpression,
                entity.CronExpression,
                entity.ScheduleSummary,
                entity.RunAsUser,
                entity.WorkingDirectory,
                entity.RunbookId,
                entity.ExecutionToken,
                entity.CommandText,
                entity.ScriptPath,
                entity.ScriptArguments,
                entity.SourcePath,
                entity.DestinationPath,
                entity.CopyRecursive,
                entity.CopyPreserveAttributes,
                entity.CopyDeleteSourceAfterCopy,
                entity.MatchPatternsCsv,
                entity.MatchCaseInsensitive,
                (ScheduledTaskAgeFilterMode)entity.AgeFilterMode,
                entity.AgeFilterValue,
                (ScheduledTaskAgeUnit)entity.AgeFilterUnit,
                entity.CleanupDeleteFiles,
                entity.CleanupDeleteDirectories,
                entity.UpdatePackageLists,
                entity.UpgradeInstalledPackages,
                entity.RemoveUnusedPackages,
                entity.CommandPreview,
                entity.CreatedAtUtc,
                entity.UpdatedAtUtc);

    private static ScheduledTaskEntity Map(ScheduledTaskDefinition task) =>
        new()
        {
            Id = task.Id,
            Name = task.Name,
            Description = task.Description,
            IsEnabled = task.IsEnabled,
            TaskKind = (int)task.TaskKind,
            ScheduleMode = (int)task.ScheduleMode,
            Minute = task.Minute,
            Hour = task.Hour,
            DayOfMonth = task.DayOfMonth,
            DaysOfWeekCsv = task.DaysOfWeekCsv,
            CustomCronExpression = task.CustomCronExpression,
            CronExpression = task.CronExpression,
            ScheduleSummary = task.ScheduleSummary,
            RunAsUser = task.RunAsUser,
            WorkingDirectory = task.WorkingDirectory,
            RunbookId = task.RunbookId,
            ExecutionToken = task.ExecutionToken,
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
            AgeFilterMode = (int)task.AgeFilterMode,
            AgeFilterValue = task.AgeFilterValue,
            AgeFilterUnit = (int)task.AgeFilterUnit,
            CleanupDeleteFiles = task.CleanupDeleteFiles,
            CleanupDeleteDirectories = task.CleanupDeleteDirectories,
            UpdatePackageLists = task.UpdatePackageLists,
            UpgradeInstalledPackages = task.UpgradeInstalledPackages,
            RemoveUnusedPackages = task.RemoveUnusedPackages,
            CommandPreview = task.CommandPreview,
            CreatedAtUtc = task.CreatedAtUtc,
            UpdatedAtUtc = task.UpdatedAtUtc
        };

    private static void Apply(ScheduledTaskEntity entity, ScheduledTaskDefinition task)
    {
        entity.Name = task.Name;
        entity.Description = task.Description;
        entity.IsEnabled = task.IsEnabled;
        entity.TaskKind = (int)task.TaskKind;
        entity.ScheduleMode = (int)task.ScheduleMode;
        entity.Minute = task.Minute;
        entity.Hour = task.Hour;
        entity.DayOfMonth = task.DayOfMonth;
        entity.DaysOfWeekCsv = task.DaysOfWeekCsv;
        entity.CustomCronExpression = task.CustomCronExpression;
        entity.CronExpression = task.CronExpression;
        entity.ScheduleSummary = task.ScheduleSummary;
        entity.RunAsUser = task.RunAsUser;
        entity.WorkingDirectory = task.WorkingDirectory;
        entity.RunbookId = task.RunbookId;
        entity.ExecutionToken = task.ExecutionToken;
        entity.CommandText = task.CommandText;
        entity.ScriptPath = task.ScriptPath;
        entity.ScriptArguments = task.ScriptArguments;
        entity.SourcePath = task.SourcePath;
        entity.DestinationPath = task.DestinationPath;
        entity.CopyRecursive = task.CopyRecursive;
        entity.CopyPreserveAttributes = task.CopyPreserveAttributes;
        entity.CopyDeleteSourceAfterCopy = task.CopyDeleteSourceAfterCopy;
        entity.MatchPatternsCsv = task.MatchPatternsCsv;
        entity.MatchCaseInsensitive = task.MatchCaseInsensitive;
        entity.AgeFilterMode = (int)task.AgeFilterMode;
        entity.AgeFilterValue = task.AgeFilterValue;
        entity.AgeFilterUnit = (int)task.AgeFilterUnit;
        entity.CleanupDeleteFiles = task.CleanupDeleteFiles;
        entity.CleanupDeleteDirectories = task.CleanupDeleteDirectories;
        entity.UpdatePackageLists = task.UpdatePackageLists;
        entity.UpgradeInstalledPackages = task.UpgradeInstalledPackages;
        entity.RemoveUnusedPackages = task.RemoveUnusedPackages;
        entity.CommandPreview = task.CommandPreview;
        entity.CreatedAtUtc = task.CreatedAtUtc;
        entity.UpdatedAtUtc = task.UpdatedAtUtc;
    }
}
