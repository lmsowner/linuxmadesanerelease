using System.Text;

namespace LinuxMadeSane.Application.Services;

public static class RunbookExecutionCommandBuilder
{
    // LMS runbooks must never block on an interactive sudo password prompt. Elevated
    // automation requires an explicitly configured runner account with passwordless
    // sudo, so sudo-marked commands use -n and fail cleanly when that contract is absent.
    private const string NonInteractiveSudo = "sudo -n";

    public static bool IsScript(string content) =>
        NormalizeContent(content).Length > 0;

    public static string NormalizeStoredScript(string content)
    {
        var normalized = NormalizeContent(content);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        if (normalized.StartsWith("#!", StringComparison.Ordinal))
        {
            return normalized;
        }

        return $"#!/usr/bin/env bash\nset -euo pipefail\n\n{normalized}";
    }

    public static string BuildSchedulerCommand(string content)
    {
        var scriptBody = NormalizeStoredScript(content);
        if (scriptBody.Length == 0)
        {
            return string.Empty;
        }

        return BuildScheduledShellScriptCommand(scriptBody, "lms-runbook");
    }

    public static string BuildScheduledShellScriptCommand(string scriptBody, string tempFilePrefix)
    {
        var normalized = NormalizeContent(scriptBody);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return BuildOneLineScheduledScriptCommand(normalized, tempFilePrefix);
    }

    public static string BuildInteractiveTerminalCommand(string content, bool requiresSudo)
    {
        var scriptBody = NormalizeStoredScript(content);
        if (scriptBody.Length == 0)
        {
            return string.Empty;
        }

        return BuildTempScriptCommand(scriptBody, requiresSudo);
    }

    public static string BuildBatchExecutionCommand(string content, bool requiresSudo)
    {
        var scriptBody = NormalizeStoredScript(content);
        if (scriptBody.Length == 0)
        {
            return string.Empty;
        }

        return BuildBatchScriptCommand(scriptBody, requiresSudo);
    }

    private static string BuildTempScriptCommand(string scriptBody, bool useSudoPrefix)
    {
        var builder = new StringBuilder();
        builder.Append("lms_runbook_tmp=\"/tmp/lms-runbook-$(date +%s)-$$.sh\"; ");
        builder.Append("cat <<'LMS_RUNBOOK_EOF' > \"$lms_runbook_tmp\"\n");
        builder.Append(scriptBody);
        if (!scriptBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("LMS_RUNBOOK_EOF\n");
        builder.Append("chmod 700 \"$lms_runbook_tmp\"; ");
        if (useSudoPrefix)
        {
            builder.Append($"{NonInteractiveSudo} bash \"$lms_runbook_tmp\"; lms_runbook_rc=$?; {NonInteractiveSudo} rm -f \"$lms_runbook_tmp\"; ");
        }
        else
        {
            builder.Append("bash \"$lms_runbook_tmp\"; lms_runbook_rc=$?; rm -f \"$lms_runbook_tmp\"; ");
        }

        builder.Append("printf '\\n[LMS runbook exit code: %s]\\n' \"$lms_runbook_rc\"");
        return builder.ToString();
    }

    private static string BuildOneLineScheduledScriptCommand(string scriptBody, string tempFilePrefix)
    {
        var encodedScript = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptBody));
        var normalizedPrefix = string.IsNullOrWhiteSpace(tempFilePrefix)
            ? "lms-script"
            : tempFilePrefix.Trim();
        return $"lms_runbook_tmp=\"/tmp/{normalizedPrefix}-$(date +%s)-$$.sh\"; printf '%s' '{encodedScript}' | base64 -d > \"$lms_runbook_tmp\"; chmod 700 \"$lms_runbook_tmp\"; bash \"$lms_runbook_tmp\"; lms_runbook_rc=$?; rm -f \"$lms_runbook_tmp\"; exit \"$lms_runbook_rc\"";
    }

    private static string BuildBatchScriptCommand(string scriptBody, bool useSudoPrefix)
    {
        var encodedScript = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptBody));
        var cleanupCommand = useSudoPrefix ? $"{NonInteractiveSudo} rm -f \"$lms_runbook_tmp\"" : "rm -f \"$lms_runbook_tmp\"";
        var executeCommand = useSudoPrefix ? $"{NonInteractiveSudo} bash \"$lms_runbook_tmp\"" : "bash \"$lms_runbook_tmp\"";
        return $"lms_runbook_tmp=\"/tmp/lms-runbook-$(date +%s)-$$.sh\"; printf '%s' '{encodedScript}' | base64 -d > \"$lms_runbook_tmp\"; chmod 700 \"$lms_runbook_tmp\"; {executeCommand}; lms_runbook_rc=$?; {cleanupCommand}; exit \"$lms_runbook_rc\"";
    }

    private static string NormalizeContent(string content) =>
        (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
}
