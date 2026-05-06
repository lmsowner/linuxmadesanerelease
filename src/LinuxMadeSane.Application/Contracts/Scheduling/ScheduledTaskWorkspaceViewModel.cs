namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskWorkspaceViewModel(
    IReadOnlyList<ScheduledTaskListItem> Tasks,
    IReadOnlyList<ScheduledTaskUserOption> UserOptions,
    IReadOnlyList<ScheduledTaskRunbookOption> Runbooks,
    ScheduledTaskHealthViewModel Health);
