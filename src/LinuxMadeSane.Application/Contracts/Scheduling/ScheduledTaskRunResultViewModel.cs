// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskRunResultViewModel(
    bool Success,
    string Summary);
