// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalSession(
    Guid Id,
    Guid HostId,
    TerminalSessionStatus Status,
    string WorkingDirectory,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc)
{
    public string Username { get; init; } = string.Empty;
}
