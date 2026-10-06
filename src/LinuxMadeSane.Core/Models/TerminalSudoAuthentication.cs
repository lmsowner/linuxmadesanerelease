// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
using System.Text.RegularExpressions;

namespace LinuxMadeSane.Core.Models;

/// <summary>Only fixed diagnostic/service operations may receive private sudo authentication.</summary>
public static partial class TerminalSudoAuthentication
{
    public const string SuccessMarker = "LMS_SUDO_AUTHENTICATION_OK";
    public const string FailureMarker = "LMS_SUDO_AUTHENTICATION_FAILED";
    public const string UpdateInspectionCommand = "sudo -n journalctl --no-pager -u linux-made-sane.service -n 80";

    public static bool TryGetOperation(string command, out string operation)
    {
        operation = string.Empty;
        if (!TryReadArguments(command, out var words) || words.Count < 2 || words[0] != "sudo") return false;
        var index = 1;
        if (words[index] == "-n") index++;
        if (index < words.Count && words[index] == "--") index++;
        if (index >= words.Count) return false;
        var program = words[index];
        var name = program.StartsWith("/usr/bin/", StringComparison.Ordinal) ? program[9..] : program;
        var arguments = words.Skip(index + 1).ToList();
        var supported = name switch
        {
            "journalctl" => IsJournalInspection(arguments),
            "systemctl" => IsServiceOperation(arguments),
            "id" or "true" or "uptime" or "uname" => arguments.All(a => a.StartsWith('-')),
            "cat" or "head" or "tail" or "ls" or "df" or "du" or "stat" or "readlink" => true,
            "/usr/local/sbin/linux-made-sane-update" => IsLmsUpdate(arguments),
            _ => false
        };
        if (!supported) return false;
        if (name is "journalctl" or "systemctl" && !arguments.Contains("--no-pager")) arguments.Add("--no-pager");
        operation = string.Join(' ', new[] { program }.Concat(arguments).Select(QuoteArgument));
        return true;
    }

    private static bool IsServiceOperation(List<string> arguments)
    {
        var values = arguments.Where(a => a is not "--no-pager" and not "--full" and not "--all" and not "--plain").ToList();
        return values.Count > 0 && values[0] switch
        {
            "status" or "is-active" or "is-enabled" or "cat" => values.Skip(1).All(a => !a.StartsWith('-')),
            "restart" or "start" or "stop" or "reload" => values.Count >= 2 && values.Skip(1).All(a => !a.StartsWith('-')),
            "list-units" or "list-unit-files" => values.Count == 1,
            _ => false
        };
    }

    private static bool IsJournalInspection(List<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "--no-pager" or "--system" or "--user" or "--utc" or "-b" or "--boot" or "--reverse" or "-r" or "--quiet" or "-q") continue;
            if (argument is "-u" or "--unit" or "-n" or "--lines" or "-p" or "--priority" or "--since" or "--until" or "-S" or "-U" or "-o" or "--output")
            {
                if (++index >= arguments.Count || arguments[index].StartsWith('-')) return false;
                if (argument is "-n" or "--lines" && !int.TryParse(arguments[index], out _)) return false;
                continue;
            }
            if (new[] { "--unit=", "--priority=", "--since=", "--until=", "--output=", "--boot=" }
                .Any(prefix => argument.StartsWith(prefix, StringComparison.Ordinal) && argument.Length > prefix.Length)) continue;
            if (argument.StartsWith("--lines=", StringComparison.Ordinal) && int.TryParse(argument[8..], out _)) continue;
            return false;
        }
        return true;
    }

    private static bool IsLmsUpdate(List<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var option = arguments[i];
            if (option == "--start") continue;
            if (i + 1 >= arguments.Count) return false;
            var value = arguments[++i];
            if (option == "--channel" && value is "stable" or "development") continue;
            if (option == "--version" && VersionValue().IsMatch(value)) continue;
            return false;
        }
        return arguments.Count > 0;
    }

    // Parse literal shell arguments without executing expansion or accepting a command list.
    // Quoted dates/paths are supported; every parsed value is re-quoted before execution.
    private static bool TryReadArguments(string? command, out List<string> words)
    {
        words = [];
        if (string.IsNullOrWhiteSpace(command)) return false;
        var value = new System.Text.StringBuilder();
        char quote = '\0';
        var started = false;
        foreach (var character in command)
        {
            if (character is '\r' or '\n' or '\0') return false;
            if (quote != '\0')
            {
                if (character == quote) { quote = '\0'; continue; }
                if (quote == '"' && character is '$' or '`' or '\\') return false;
                value.Append(character); continue;
            }
            if (character is '\'' or '"') { quote = character; started = true; continue; }
            if (char.IsWhiteSpace(character))
            {
                if (started) { words.Add(value.ToString()); value.Clear(); started = false; }
                continue;
            }
            if (character is '$' or '`' or ';' or '|' or '&' or '<' or '>' or '(' or ')' or '\\' or '#' or '*') return false;
            value.Append(character); started = true;
        }
        if (quote != '\0') return false;
        if (started) words.Add(value.ToString());
        return words.Count > 0;
    }

    private static string QuoteArgument(string value) => SafeArguments().IsMatch(value) && !value.Contains(' ')
        ? value : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public static string BuildCommand(string command)
    {
        if (!TryGetOperation(command, out var operation))
            throw new InvalidOperationException("Private authentication supports a single diagnostic or service command. Shell scripts, pipelines and other commands must use the interactive terminal.");
        // Both sudo invocations have the same shell parent. Never send secret stdin to
        // the requested operation, even if sudo timestamp policy refuses reuse.
        return "export SYSTEMD_PAGER=cat SYSTEMD_COLORS=0; if /usr/bin/sudo -S -p '' -v; then printf '%s\\n' '" + SuccessMarker + "' >&2; exec </dev/null; /usr/bin/sudo -n -- " + operation + "; exit $?; else printf '%s\\n' '" + FailureMarker + "' >&2; exit 77; fi";
    }

    [GeneratedRegex(@"\Av[0-9]{4}(?:\.[0-9]{2}){4}\z")]
    private static partial Regex VersionValue();

    [GeneratedRegex(@"\A[A-Za-z0-9_./:@%=,+\- ]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeArguments();
}
