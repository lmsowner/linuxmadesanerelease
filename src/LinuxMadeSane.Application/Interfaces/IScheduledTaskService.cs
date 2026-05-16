// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Scheduling;

namespace LinuxMadeSane.Application.Interfaces;

public interface IScheduledTaskService
{
    Task<ScheduledTaskWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<ScheduledTaskEditor> GetEditorAsync(Guid? taskId, CancellationToken cancellationToken = default);

    Task<Guid> SaveTaskAsync(ScheduledTaskEditor editor, CancellationToken cancellationToken = default);

    Task DeleteTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<ScheduledTaskRunResultViewModel> RunTaskNowAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<ScheduledTaskRunResultViewModel> TriggerTaskAsync(Guid taskId, string executionToken, CancellationToken cancellationToken = default);

    Task<ScheduledTaskLogViewModel> GetTaskLogAsync(Guid taskId, CancellationToken cancellationToken = default);
}
