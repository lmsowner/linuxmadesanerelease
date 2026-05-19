// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record HostConnectionTestResult(
    ConnectionTestStatus Status,
    string Summary,
    string? Detail,
    DateTimeOffset CheckedAtUtc);
