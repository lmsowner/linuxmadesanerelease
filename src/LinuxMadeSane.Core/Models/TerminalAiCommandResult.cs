// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
}
