// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed record ManagedHostLmsInstallResult(
    bool Success,
    string Summary,
    string Detail,
    string CommandText,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
