// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record FileSearchProgress(
    string CurrentPath,
    DateTimeOffset ReportedAtUtc,
    FileSearchMatch? Match = null);
