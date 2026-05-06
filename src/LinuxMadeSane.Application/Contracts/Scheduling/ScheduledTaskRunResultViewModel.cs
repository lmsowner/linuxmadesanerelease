namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskRunResultViewModel(
    bool Success,
    string Summary);
