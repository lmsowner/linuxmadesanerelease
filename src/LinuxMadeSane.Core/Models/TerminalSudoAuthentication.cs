// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Core.Models;

/// <summary>Only fixed diagnostic/service operations may receive private sudo authentication.</summary>
public static partial class TerminalSudoAuthentication
{
    public const string FailureMarker = "LMS_SUDO_AUTHENTICATION_FAILED";
    public const string UpdateInspectionCommand = "sudo -n journalctl --no-pager -u linux-made-sane.service -n 80";

    public static bool TryGetOperation(string command, out string operation)
    {
        operation = string.Empty;
        // Deliberately exclude shell syntax, quotes, substitutions, newlines and sudo options
        // that could redirect the password to an alternative consumer.
        if (string.IsNullOrEmpty(command) || !SafeArguments().IsMatch(command)) return false;
        var words = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0] != "sudo") return false;
        var index = 1;
        if (words[index] == "-n") index++;
        if (index >= words.Length) return false;
        var program = words[index];
        var arguments = words[(index + 1)..];
        var supported = program switch
        {
            "journalctl" or "/usr/bin/journalctl" => IsJournalInspection(arguments),
            "systemctl" or "/usr/bin/systemctl" => arguments.Length >= 2 &&
                arguments[0] is "status" or "is-active" or "is-enabled" or "restart" or "start" or "stop" &&
                arguments.Skip(1).All(a => !a.StartsWith('-')),
            "id" or "/usr/bin/id" or "true" or "/usr/bin/true" => arguments.Length == 0,
            _ => false
        };
        if (!supported) return false;
        operation = string.Join(' ', words[index..]);
        return true;
    }

    private static bool IsJournalInspection(string[] arguments)
    {
        if (!arguments.Contains("--no-pager")) return false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is "--no-pager" or "--system" or "--user" or "--utc" or "-b" or "--boot") continue;
            if (argument is "-u" or "--unit" or "-n" or "--lines" or "-p" or "--priority")
            {
                if (++index >= arguments.Length || arguments[index].StartsWith('-')) return false;
                if (argument is "-n" or "--lines" && !int.TryParse(arguments[index], out _)) return false;
                continue;
            }
            if (argument.StartsWith("--unit=", StringComparison.Ordinal) && argument.Length > 7) continue;
            if (argument.StartsWith("--lines=", StringComparison.Ordinal) && int.TryParse(argument[8..], out _)) continue;
            return false;
        }
        return true;
    }

    public static string BuildCommand(string command)
    {
        if (!TryGetOperation(command, out var operation))
            throw new InvalidOperationException("Private authentication supports a single diagnostic or service command. Shell scripts, pipelines and other commands must use the interactive terminal.");
        // Both sudo invocations have the same shell parent. Never send secret stdin to
        // the requested operation, even if sudo timestamp policy refuses reuse.
        return "export SYSTEMD_PAGER=cat SYSTEMD_COLORS=0; if /usr/bin/sudo -S -p '' -v; then exec </dev/null; /usr/bin/sudo -n -- " + operation + "; exit $?; else printf '%s\\n' '" + FailureMarker + "' >&2; exit 77; fi";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_./:@%=,+\- ]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeArguments();
}
