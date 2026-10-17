// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalSessionOutputAppended(
    Guid SessionId,
    string Chunk,
    long OutputRevision,
    TerminalSessionStatus Status,
    DateTimeOffset LastActivityUtc);
