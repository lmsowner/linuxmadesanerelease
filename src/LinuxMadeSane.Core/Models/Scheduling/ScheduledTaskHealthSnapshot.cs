namespace LinuxMadeSane.Core.Models.Scheduling;

public sealed record ScheduledTaskHealthSnapshot(
    bool CronDirectoryAvailable,
    bool CrontabBinaryAvailable,
    string DetectedServiceName,
    string ServiceState,
    string Summary);
