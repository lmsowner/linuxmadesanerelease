// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Scheduling;

public static class ScheduledTaskTrigger
{
    public const string HeaderName = "X-LMS-Task-Token";

    public static string GetPath(Guid taskId) =>
        $"/internal/scheduler/tasks/{taskId:D}/run";
}
