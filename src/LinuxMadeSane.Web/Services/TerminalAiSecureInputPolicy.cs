// Copyright (c) Linux Made Sane.
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
        preparedCommand = commandText;
        return TerminalSudoAuthentication.TryGetOperation(commandText, out _);
    }

    public static bool HasSudoAuthenticationFailure(string? failureText) =>
        SudoPasswordFailure().IsMatch(failureText ?? string.Empty);

    [GeneratedRegex(@"^(?<indent>\s*)sudo(?<rest>(?:\s.*)?)$", RegexOptions.Singleline)]
    private static partial Regex StartsWithSudo();

    [GeneratedRegex(
        @"(?:interactive authentication is required|LMS_ADMIN_ACCESS_REQUIRED|LMS_SUDO_AUTHENTICATION_FAILED|a password is required|a terminal is required to read the password|no interactive terminal|no tty present|sudo[^\r\n.]{0,80}(?:could not authenticate|administrator access failed|authentication failed)|\[sudo\]\s*password|password for\s+[^:\r\n]+:)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SudoPasswordFailure();
}
