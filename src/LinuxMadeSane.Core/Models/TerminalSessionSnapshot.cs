// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record TerminalSessionSnapshot(
    Guid SessionId,
    TerminalSessionStatus Status,
    string WorkingDirectory,
    string Output,
    long OutputRevision,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc);
