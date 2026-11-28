// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Scheduling;
namespace LinuxMadeSane.Core.Abstractions;

// Extend the existing scheduler for operations that must run through LMS services
// (for example resolving a protected backup secret), rather than exposing it to cron.
public interface IScheduledTaskHandler
{
    bool CanHandle(ScheduledTaskDefinition task);
    Task<ScheduledTaskRunResult> ExecuteAsync(ScheduledTaskDefinition task, CancellationToken token);
}
