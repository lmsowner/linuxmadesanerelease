namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskUserOption(
    string UserName,
    string DisplayLabel,
    string Description,
    bool IsRoot);
