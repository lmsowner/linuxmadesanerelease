// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Scheduling;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Scheduling;

namespace LinuxMadeSane.Application.Services;

public sealed record ScriptExecutionStage(string Label, string Path, string Arguments, string WorkingDirectory);
public sealed record ScriptExecutionReport(bool Success, string Summary);

public static class ScheduledScriptExecution
{
    public static ScheduledScriptOutcomes Snapshot(ScheduledScriptOutcomes settings) =>
        JsonSerializer.Deserialize<ScheduledScriptOutcomes>(JsonSerializer.Serialize(settings))!;

    public static bool IsInterruptedOrUnavailable(int code) => code is 124 or 126 or 127 or >= 128;

    public static string BuildCommand(ScriptExecutionStage stage, string user)
    {
        var command = ScheduledTaskCompiler.Compile(new ScheduledTaskEditor
        { Name = stage.Label, TaskKind = ScheduledTaskKind.ShellScript, ScriptPath = stage.Path,
          ScriptArguments = stage.Arguments, RunAsUser = user }).CommandPreview;
        var path = Quote(stage.Path);
        var directory = string.IsNullOrWhiteSpace(stage.WorkingDirectory) ? string.Empty : $"cd {Quote(stage.WorkingDirectory)} || exit 126; ";
        return directory + $"if [[ ! -f {path} || ! -r {path} ]]; then printf '%s\\n' 'LMS: Script is missing or unreadable.' >&2; exit 126; fi; " + command;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static IEnumerable<string> Validate(ScheduledScriptOutcomes settings)
    {
        if (settings.ExitCodes.Count > 32 || settings.FollowUps.Count > 16)
            yield return "Use at most 32 exit-code meanings and 16 follow-up rules.";
        if (settings.ExitCodes.GroupBy(x => x.Code).Any(x => x.Count() > 1))
            yield return "Each exit code can have only one meaning.";
        foreach (var meaning in settings.ExitCodes)
        {
            if (meaning.Code is < 0 or > 255 || !Enum.IsDefined(meaning.Disposition))
                yield return "Exit codes must be between 0 and 255 with a valid outcome.";
            if (IsInterruptedOrUnavailable(meaning.Code) && meaning.Disposition != ScriptExitDisposition.Error)
                yield return "Codes 124, 126, 127 and 128–255 are reserved for timeout, launch errors and interruptions.";
            if (string.IsNullOrWhiteSpace(meaning.Description) || meaning.Description.Length > 160 || meaning.Description.Any(char.IsControl))
                yield return "Give each exit code a short, single-line description (up to 160 characters).";
        }
        if (settings.FollowUps.GroupBy(x => (x.Condition, x.Condition == ScriptFollowUpCondition.ExitCode ? x.ExitCode : -1)).Any(x => x.Count() > 1))
            yield return "Choose only one follow-up for each condition or specific exit code.";
        foreach (var rule in settings.FollowUps)
        {
            if (!Enum.IsDefined(rule.Condition) || rule.ExitCode is < 0 or > 255)
                yield return "Choose a valid follow-up condition and exit code between 0 and 255.";
            if (rule.Condition == ScriptFollowUpCondition.ExitCode && IsInterruptedOrUnavailable(rule.ExitCode))
                yield return "A follow-up cannot run after a timeout, launch error or interruption.";
            if (!rule.ScriptPath.StartsWith('/') || rule.ScriptPath.Any(char.IsControl))
                yield return "Choose an absolute follow-up script path.";
            if (rule.Arguments.Contains('\n') || rule.Arguments.Contains('\r') || rule.Arguments.Contains('\0'))
                yield return "Follow-up arguments must stay on one line.";
            if ((!string.IsNullOrWhiteSpace(rule.WorkingDirectory) && !rule.WorkingDirectory.StartsWith('/')) || rule.WorkingDirectory.Any(char.IsControl))
                yield return "The follow-up working directory must be an absolute path.";
        }
    }

    public static (bool Accepted, string Summary) Describe(int code, ScheduledScriptOutcomes settings)
    {
        if (IsInterruptedOrUnavailable(code)) return (false, code switch
        { 124 => "Run timed out (exit code 124)", 126 or 127 => $"Script could not be launched (exit code {code})", _ => $"Run interrupted (exit code {code})" });
        var meaning = settings.ExitCodes.FirstOrDefault(x => x.Code == code);
        var accepted = meaning is null ? code == 0 : meaning.Disposition != ScriptExitDisposition.Error;
        var description = meaning?.Description ?? (code == 0 ? "Completed" : "Exited");
        return (accepted, $"{description} (exit code {code})");
    }

    public static async Task<ScriptExecutionReport> RunAsync(
        ScriptExecutionStage primary, ScheduledScriptOutcomes settings,
        Func<ScriptExecutionStage, CancellationToken, Task<LinuxCommandResult>> execute,
        CancellationToken cancellationToken)
    {
        settings = Snapshot(settings);
        var errors = Validate(settings).ToArray();
        if (errors.Length > 0) throw new ValidationException(string.Join(" ", errors));
        cancellationToken.ThrowIfCancellationRequested();
        var result = await execute(primary, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = Describe(result.ExitCode, settings);
        var summary = $"Script: {outcome.Summary}.";
        // An exact match overrides the general success/other rule. Only one
        // follow-up is executed; it cannot trigger another follow-up.
        if (IsInterruptedOrUnavailable(result.ExitCode)) return new(false, summary);
        var next = settings.FollowUps.FirstOrDefault(x => x.Condition == ScriptFollowUpCondition.ExitCode && x.ExitCode == result.ExitCode)
            ?? settings.FollowUps.FirstOrDefault(x => x.Condition == (outcome.Accepted ? ScriptFollowUpCondition.Success : ScriptFollowUpCondition.Other));
        if (next is null) return new(outcome.Accepted, summary);
        cancellationToken.ThrowIfCancellationRequested();
        var followUp = await execute(new("Follow-up", next.ScriptPath, next.Arguments,
            string.IsNullOrWhiteSpace(next.WorkingDirectory) ? System.IO.Path.GetDirectoryName(next.ScriptPath)! : next.WorkingDirectory), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var followUpSuccess = followUp.ExitCode == 0;
        return new(outcome.Accepted && followUpSuccess,
            summary + $" Follow-up: {(followUpSuccess ? "Completed" : "Exited")} (exit code {followUp.ExitCode}).");
    }
}
