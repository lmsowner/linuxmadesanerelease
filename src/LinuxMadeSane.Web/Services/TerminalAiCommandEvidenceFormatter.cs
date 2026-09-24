// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Text;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Web.Services;

public static class TerminalAiCommandEvidenceFormatter
{
    private const int MaxOutputCharsPerChannel = 6_000;

    public static string Format(TerminalAiCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.AppendLine("Private command channel result:");
        builder.AppendLine($"Command: {result.CommandText}");
        builder.AppendLine($"Executed as: {result.ExecutedAsUser}");
        builder.AppendLine($"Working directory: {result.WorkingDirectory}");
        builder.AppendLine($"Exit code: {result.ExitCode}");
        builder.AppendLine($"Duration: {(result.CompletedAtUtc - result.StartedAtUtc).TotalMilliseconds:0} ms");
        builder.AppendLine("Standard output:");
        builder.AppendLine(TrimChannel(result.StandardOutput));
        builder.AppendLine("Standard error:");
        builder.Append(TrimChannel(result.StandardError));
        return builder.ToString().TrimEnd();
    }

    private static string TrimChannel(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= MaxOutputCharsPerChannel
            ? value
            : $"[earlier output omitted]{Environment.NewLine}{value[^MaxOutputCharsPerChannel..]}";
    }
}
