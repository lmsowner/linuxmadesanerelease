namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskHealthViewModel(
    bool CronDirectoryAvailable,
    bool CrontabBinaryAvailable,
    string DetectedServiceName,
    string ServiceState,
    string Summary);
