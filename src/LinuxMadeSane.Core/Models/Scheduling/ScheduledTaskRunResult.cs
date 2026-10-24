// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Scheduling;

public sealed record ScheduledTaskRunResult(
    bool Success,
    string Summary);
