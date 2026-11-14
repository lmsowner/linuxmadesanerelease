// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
