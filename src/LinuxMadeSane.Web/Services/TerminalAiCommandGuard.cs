// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;

namespace LinuxMadeSane.Web.Services;

public static class TerminalAiCommandGuard
{
    private static readonly string[] BlockedShellOperators =
    [
        "&&",
        "||",
        ";",
        "|",
        "&",
        ">",
        ">>",
        "<<",
        "| tee",
        "`",
        "$(",
        "\r",
        "\n"
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
        "printenv",
        "df",
        "du",
        "free",
        "uptime",
        "who",
        "w",
        "lsblk",
        "lspci",
        "lsusb",
        "docker",
        "bluetoothctl",
        "tailscale",
        "nvidia-smi",
        "apt",
        "apt-cache",
        "dpkg-query",
        "loginctl",
        "ps",
        "ss",
        "ip",
        "localectl",
        "setxkbmap",
        "xfconf-query",
        "gsettings",
        "systemctl",
        "sed"
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
            "hostname" => ValidateHostname(tokens, out reason),
            "hostnamectl" => ValidateHostnameCtl(tokens, out reason),
            "journalctl" => ValidateJournalCtl(tokens, out reason),
            "docker" => ValidateDocker(tokens, out reason),
            "bluetoothctl" => ValidateBluetoothCtl(tokens, out reason),
            "tailscale" => ValidateTailscale(tokens, out reason),
            "nvidia-smi" => ValidateNvidiaSmi(tokens, out reason),
            "apt" => ValidateApt(tokens, out reason),
            "loginctl" => ValidateLoginCtl(tokens, out reason),
            "find" => ValidateFind(tokens, out reason),
            "rg" => ValidateRipgrep(tokens, out reason),
            "ip" => ValidateIp(tokens, out reason),
            "localectl" => ValidateLocalectl(tokens, out reason),
            "setxkbmap" => ValidateSetXkbMap(tokens, out reason),
            "xfconf-query" => ValidateXfconfQuery(tokens, out reason),
            "gsettings" => ValidateGSettings(tokens, out reason),
            "sed" => ValidateSed(tokens, out reason),
            _ => true
        };
    }

    private static bool ValidateDocker(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var verb = tokens.Skip(1).FirstOrDefault(token => !token.StartsWith('-'));
        var readOnlyVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ps",
            "images",
            "info",
            "inspect",
            "logs",
            "stats",
            "top",
            "version"
        };

        if (verb is not null && readOnlyVerbs.Contains(verb))
        {
            return true;
        }

        reason = "docker command was not read-only";
        return false;
    }

    private static bool ValidateBluetoothCtl(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var verb = tokens.Skip(1).FirstOrDefault(token => !token.StartsWith('-'));
        if (verb is not null && new[] { "devices", "paired-devices", "show", "info", "list" }
                .Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        reason = "bluetoothctl command was not read-only";
        return false;
    }

    private static bool ValidateTailscale(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Count >= 2 && tokens[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        reason = "tailscale command was not read-only";
        return false;
    }

    private static bool ValidateNvidiaSmi(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var readOnlyOptions = new[]
        {
            "-q",
            "--query",
            "-L",
            "--list-gpus",
            "-B",
            "--list-excluded-gpus",
            "--query-gpu=",
            "--query-compute-apps=",
            "--query-accounted-apps=",
            "--query-retired-pages=",
            "--query-remapped-rows=",
            "--format=",
            "--id="
        };

        if (tokens.Skip(1).All(token =>
                readOnlyOptions.Any(option => option.EndsWith('=')
                    ? token.StartsWith(option, StringComparison.OrdinalIgnoreCase)
                    : token.Equals(option, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        reason = "nvidia-smi options were not a known read-only query";
        return false;
    }

    private static bool ValidateApt(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Count >= 2 &&
            (tokens[1].Equals("list", StringComparison.OrdinalIgnoreCase) ||
             tokens[1].Equals("show", StringComparison.OrdinalIgnoreCase) ||
             tokens[1].Equals("search", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        reason = "apt command was not read-only";
        return false;
    }

    private static bool ValidateHostname(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var readOnlyOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-a",
            "--alias",
            "-A",
            "--all-fqdns",
            "-d",
            "--domain",
            "-f",
            "--fqdn",
            "--long",
            "-i",
            "--ip-address",
            "-I",
            "--all-ip-addresses",
            "-s",
            "--short",
            "-y",
            "--yp",
            "--nis",
            "-h",
            "--help",
            "-V",
            "--version"
        };

        if (tokens.Skip(1).All(readOnlyOptions.Contains))
        {
            return true;
        }

        reason = "hostname with a name argument changes host state";
        return false;
    }

    private static bool ValidateHostnameCtl(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Count == 1 || tokens[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        reason = "hostnamectl command was not read-only";
        return false;
    }

    private static bool ValidateJournalCtl(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var mutatingOptions = new[]
        {
            "--flush",
            "--rotate",
            "--setup-keys",
            "--sync",
            "--update-catalog",
            "--vacuum-",
            "--relinquish-var",
            "--smart-relinquish-var"
        };

        if (tokens.Skip(1).Any(token =>
                mutatingOptions.Any(option => token.StartsWith(option, StringComparison.OrdinalIgnoreCase))))
        {
            reason = "journalctl command changes journal state";
            return false;
        }

        return true;
    }

    private static bool ValidateLoginCtl(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Count == 1)
        {
            return true;
        }

        var verb = tokens[1];
        if (verb.Equals("status", StringComparison.OrdinalIgnoreCase) ||
            verb.StartsWith("show-", StringComparison.OrdinalIgnoreCase) ||
            verb.StartsWith("list-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        reason = "loginctl command was not read-only";
        return false;
    }

    private static bool ValidateFind(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var mutatingExpressions = new[]
        {
            "-delete",
            "-exec",
            "-execdir",
            "-ok",
            "-okdir",
            "-fprint",
            "-fprint0",
            "-fprintf",
            "-fls"
        };

        if (tokens.Skip(1).Any(token =>
                mutatingExpressions.Any(expression => token.Equals(expression, StringComparison.OrdinalIgnoreCase))))
        {
            reason = "find command can change files or run another command";
            return false;
        }

        return true;
    }

    private static bool ValidateRipgrep(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        if (tokens.Skip(1).Any(token => token.Equals("--pre", StringComparison.OrdinalIgnoreCase) ||
                                         token.StartsWith("--pre=", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "ripgrep preprocessor can run another command";
            return false;
        }

        return true;
    }

    private static bool ValidateIp(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var mutatingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "add",
            "append",
            "batch",
            "change",
            "delete",
            "del",
            "exec",
            "flush",
            "monitor",
            "replace",
            "restore",
            "set"
        };

        if (tokens.Skip(1).Any(mutatingVerbs.Contains))
        {
            reason = "ip command was not read-only";
            return false;
        }

        return true;
    }

    private static bool ValidateSystemctl(IReadOnlyList<string> tokens, out string reason)
    {
        reason = string.Empty;
        var verb = tokens.Skip(1).FirstOrDefault(token => !token.StartsWith('-'));
        if (verb is null)
        {
            return true;
        }
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
                token.Equals("-T", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("--toggle", StringComparison.OrdinalIgnoreCase) ||
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

        var script = tokens.Skip(1)
            .FirstOrDefault(token => !token.StartsWith('-'))?
            .Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(script) ||
            !Regex.IsMatch(
                script,
                @"^(?:(?:\d+|\$)(?:,(?:\d+|\$))?[p=q]?|/[^/\r\n]+/[p=])$",
                RegexOptions.CultureInvariant))
        {
            reason = "sed script was not a known read-only print expression";
            return false;
        }

        return true;
    }
}
