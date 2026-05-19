// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
