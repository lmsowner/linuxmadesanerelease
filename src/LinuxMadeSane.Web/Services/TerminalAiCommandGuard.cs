namespace LinuxMadeSane.Web.Services;

public static class TerminalAiCommandGuard
{
    private static readonly string[] BlockedShellOperators =
    [
        "&&",
        "||",
        ";",
        ">",
        ">>",
        "<<",
        "| tee",
        "`",
        "$("
    ];

    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat",
        "grep",
        "rg",
        "find",
        "ls",
        "pwd",
        "whoami",
        "id",
        "hostname",
        "hostnamectl",
        "uname",
        "journalctl",
        "tail",
        "head",
        "stat",
        "file",
        "readlink",
        "env",
        "printenv",
        "loginctl",
        "ps",
        "ss",
        "ip",
        "localectl",
        "setxkbmap",
        "xfconf-query",
        "gsettings",
        "systemctl",
        "sed",
        "awk"
    };

    public static bool TryValidateForInvestigation(string commandText, out string reason)
    {
        reason = string.Empty;

        var normalized = commandText.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            reason = "the command was empty";
            return false;
        }

        foreach (var blockedOperator in BlockedShellOperators)
        {
            if (normalized.Contains(blockedOperator, StringComparison.Ordinal))
            {
                reason = "it contains shell chaining or redirection";
                return false;
            }
        }

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (tokens.Count == 0)
        {
            reason = "the command was empty";
            return false;
        }

        if (tokens[0].Equals("sudo", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
            if (tokens.Count == 0)
            {
                reason = "it only contained sudo";
                return false;
            }
        }

        var command = tokens[0];
        if (!ReadOnlyCommands.Contains(command))
        {
            reason = "it is not a known read-only diagnostic command";
            return false;
        }

        return command.ToLowerInvariant() switch
        {
            "systemctl" => ValidateSystemctl(tokens, out reason),
            "localectl" => ValidateLocalectl(tokens, out reason),
            "setxkbmap" => ValidateSetXkbMap(tokens, out reason),
            "xfconf-query" => ValidateXfconfQuery(tokens, out reason),
            "gsettings" => ValidateGSettings(tokens, out reason),
            "sed" => ValidateSed(tokens, out reason),
            _ => true
        };
    }

    private static bool ValidateSystemctl(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Count < 2)
        {
            reason = "systemctl needs a read-only subcommand";
            return false;
        }

        var verb = tokens[1];
        if (verb.Equals("status", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("show", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("cat", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("is-active", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("is-enabled", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("list-units", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("list-unit-files", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        reason = "systemctl command was not read-only";
        return false;
    }

    private static bool ValidateLocalectl(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Count < 2)
        {
            reason = "localectl needs a read-only subcommand";
            return false;
        }

        var verb = tokens[1];
        if (verb.Equals("status", StringComparison.OrdinalIgnoreCase) ||
            verb.StartsWith("list-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        reason = "localectl command was not read-only";
        return false;
    }

    private static bool ValidateSetXkbMap(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Any(token => token.Equals("-query", StringComparison.OrdinalIgnoreCase) || token.Equals("-print", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        reason = "setxkbmap command changes layout state";
        return false;
    }

    private static bool ValidateXfconfQuery(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Any(token =>
                token.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("--set", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("--reset", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("-R", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("--create", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("-n", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "xfconf-query command was not read-only";
            return false;
        }

        return true;
    }

    private static bool ValidateGSettings(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Count < 2)
        {
            reason = "gsettings needs a read-only subcommand";
            return false;
        }

        var verb = tokens[1];
        if (verb.Equals("get", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("list-keys", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("list-recursively", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("list-schemas", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("range", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("writable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        reason = "gsettings command was not read-only";
        return false;
    }

    private static bool ValidateSed(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Any(token => token.Equals("-i", StringComparison.OrdinalIgnoreCase) || token.StartsWith("-i", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "sed command edits files in place";
            return false;
        }

        return true;
    }
}
