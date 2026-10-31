// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Models.Scheduling;

public enum ScriptExitDisposition { Success, Information, Error }
public enum ScriptFollowUpCondition { Success, Other, ExitCode }

public sealed class ScriptExitMeaning
{
    public int Code { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public ScriptExitDisposition Disposition { get; set; } = ScriptExitDisposition.Information;
}

public sealed class ScriptFollowUp
{
    public ScriptFollowUpCondition Condition { get; set; }
    public int ExitCode { get; set; } = 1;
    public string ScriptPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}

public sealed class ScheduledScriptOutcomes
{
    public List<ScriptExitMeaning> ExitCodes { get; set; } = [];
    public List<ScriptFollowUp> FollowUps { get; set; } = [];
}
