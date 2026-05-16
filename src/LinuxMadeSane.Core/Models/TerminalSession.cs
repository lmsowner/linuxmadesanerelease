// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalSession(
    Guid Id,
    Guid HostId,
    TerminalSessionStatus Status,
    string WorkingDirectory,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc);
