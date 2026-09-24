// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text.RegularExpressions;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public static partial class TerminalAiSecureInputPolicy
{
    public static bool RequiresSudoPassword(string commandText, TerminalAiCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess || !StartsWithSudo().IsMatch(commandText ?? string.Empty))
        {
            return false;
        }

        var failureText = $"{result.StandardError}\n{result.StandardOutput}";
        return SudoPasswordFailure().IsMatch(failureText);
    }

    public static bool TryPrepareSudoPasswordCommand(string commandText, out string preparedCommand)
    {
        preparedCommand = string.Empty;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return false;
        }

        var match = StartsWithSudo().Match(commandText);
        if (!match.Success)
        {
            return false;
        }

        var rest = StandaloneNonInteractiveOption().Replace(match.Groups["rest"].Value, string.Empty);
        var options = SudoReadsStandardInput().IsMatch(rest)
            ? "sudo -p ''"
            : "sudo -S -p ''";
        preparedCommand = $"{match.Groups["indent"].Value}{options}{rest}";
        return true;
    }

    [GeneratedRegex(@"^(?<indent>\s*)sudo(?<rest>(?:\s.*)?)$", RegexOptions.Singleline)]
    private static partial Regex StartsWithSudo();

    [GeneratedRegex(@"(?<!\S)-n(?!\S)")]
    private static partial Regex StandaloneNonInteractiveOption();

    [GeneratedRegex(@"(?<!\S)-[A-Za-z]*S[A-Za-z]*(?!\S)")]
    private static partial Regex SudoReadsStandardInput();

    [GeneratedRegex(
        @"(?:a password is required|a terminal is required to read the password|no tty present|\[sudo\]\s*password|password for\s+[^:\r\n]+:)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SudoPasswordFailure();
}
