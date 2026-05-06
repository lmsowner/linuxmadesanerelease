namespace LinuxMadeSane.Core.Models.Scheduling;

public sealed record ScheduledTaskLogSnapshot(
    Guid TaskId,
    string TaskName,
    string LogFilePath,
    bool Exists,
    string Content,
    DateTimeOffset? LastUpdatedAtUtc,
    bool IsTruncated);
