// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Scheduling;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILinuxSchedulingModuleDataService
{
    Task<IReadOnlyList<ScheduledTaskDefinition>> ListTasksAsync(CancellationToken cancellationToken = default);

    Task<ScheduledTaskDefinition?> GetTaskAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveTaskAsync(ScheduledTaskDefinition task, CancellationToken cancellationToken = default);

    Task DeleteTaskAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ScheduledTaskRunResult> RunTaskNowAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ScheduledTaskRunResult> TriggerTaskAsync(Guid id, string executionToken, CancellationToken cancellationToken = default);

    Task<ScheduledTaskLogSnapshot> GetTaskLogAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ScheduledTaskHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default);
}
