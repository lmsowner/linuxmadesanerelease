// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

internal static class SftpSystemCommandHelper
{
    internal static OperationLogEntry MapLog(LinuxCommandResult result, string message) =>
        new(
            result.CompletedAt,
            result.ExitCode == 0 ? OperationLogLevel.Info : OperationLogLevel.Error,
            message,
            result.CommandText,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);

    internal static OperationLogEntry MapMessageLog(string message, string? commandText = null) =>
        new(DateTimeOffset.UtcNow, OperationLogLevel.Info, message, commandText, null, null, null);

    internal static string BuildFailureDetail(LinuxCommandResult result) =>
        FirstNonEmptyLine(result.StandardError, result.StandardOutput) ?? $"exit code {result.ExitCode}";

    internal static string? FirstNonEmptyLine(params string[] values) =>
        values
            .SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    internal static string QuoteShellArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('\'') || value.Contains('"')
            ? $"'{value.Replace("'", "'\"'\"'")}'"
            : value;
    }
}
