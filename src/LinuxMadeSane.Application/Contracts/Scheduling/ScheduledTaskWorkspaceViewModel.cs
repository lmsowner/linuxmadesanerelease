// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskWorkspaceViewModel(
    IReadOnlyList<ScheduledTaskListItem> Tasks,
    IReadOnlyList<ScheduledTaskUserOption> UserOptions,
    IReadOnlyList<ScheduledTaskRunbookOption> Runbooks,
    ScheduledTaskHealthViewModel Health);
