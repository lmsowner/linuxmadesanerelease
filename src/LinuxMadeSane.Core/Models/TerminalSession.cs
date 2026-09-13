// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
