// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalAiCommandResult(
    Guid RequestId,
    Guid TerminalSessionId,
    string CommandText,
    string ExecutedAsUser,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    public bool IsSuccess => ExitCode == 0;
    public bool AdministratorAuthenticated { get; init; }
}
