namespace LinuxMadeSane.Core.Models.Scheduling;

public static class ScheduledTaskPaths
{
    public const string CronDirectoryPath = "/etc/cron.d";
    public const string LogDirectoryPath = "/var/log/linuxmadesane/scheduled-tasks";

    public static string GetCronFilePath(Guid taskId) =>
        Path.Combine(CronDirectoryPath, $"linuxmadesane-schedule-{taskId:N}");

    public static string GetLogFilePath(Guid taskId) =>
        Path.Combine(LogDirectoryPath, $"{taskId:N}.log");
}
