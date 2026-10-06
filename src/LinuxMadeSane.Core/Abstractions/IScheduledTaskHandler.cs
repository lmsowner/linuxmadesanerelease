// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Scheduling;
namespace LinuxMadeSane.Core.Abstractions;

// Extend the existing scheduler for operations that must run through LMS services
// (for example resolving a protected backup secret), rather than exposing it to cron.
public interface IScheduledTaskHandler
{
    bool CanHandle(ScheduledTaskDefinition task);
    Task<ScheduledTaskRunResult> ExecuteAsync(ScheduledTaskDefinition task, CancellationToken token);
}
