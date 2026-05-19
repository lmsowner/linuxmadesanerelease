// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
