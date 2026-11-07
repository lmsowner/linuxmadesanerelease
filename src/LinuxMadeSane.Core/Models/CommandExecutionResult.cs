// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record CommandExecutionResult(
    string CommandText,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc)
{
    public bool IsSuccess => ExitCode == 0;
}
