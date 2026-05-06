namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskLogViewModel(
    Guid TaskId,
    string TaskName,
    string LogFilePath,
    bool Exists,
    string Content,
    DateTimeOffset? LastUpdatedAtUtc,
    bool IsTruncated);
