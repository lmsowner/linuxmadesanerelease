// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts;

public enum RunbookExecutionProgressKind
{
    Queued = 0,
    Started = 1,
    StandardOutput = 2,
    StandardError = 3,
    Completed = 4,
    Failed = 5
}
