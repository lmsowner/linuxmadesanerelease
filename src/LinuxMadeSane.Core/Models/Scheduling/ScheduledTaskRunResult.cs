namespace LinuxMadeSane.Core.Models.Scheduling;

public sealed record ScheduledTaskRunResult(
    bool Success,
    string Summary);
