// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed record RunbookExecutionHostResultViewModel(
    Guid HostId,
    string HostName,
    bool Success,
    int ExitCode,
    string Summary,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
