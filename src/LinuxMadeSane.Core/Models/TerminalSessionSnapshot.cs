// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalSessionSnapshot(
    Guid SessionId,
    TerminalSessionStatus Status,
    string WorkingDirectory,
    string Output,
    long OutputRevision,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc)
{
    public string Username { get; init; } = string.Empty;
}
