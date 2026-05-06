namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskRunbookOption(
    Guid Id,
    string Name,
    string Description,
    string CommandText,
    bool RequiresSudo,
    bool IsQuickAccess)
{
    public bool IsScript =>
        CommandText.Contains('\n') || CommandText.Contains('\r');
}
