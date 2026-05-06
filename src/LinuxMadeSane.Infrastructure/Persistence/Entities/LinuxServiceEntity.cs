namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LinuxServiceEntity
{
    public Guid Id { get; set; }
    public string UnitName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int RuntimeState { get; set; }
    public int HealthStatus { get; set; }
    public bool EnabledAtBoot { get; set; }
    public bool ActiveUnderSystemd { get; set; }
    public string RunningUser { get; set; } = string.Empty;
    public string RunningGroup { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string ExecStart { get; set; } = string.Empty;
    public string? EnvironmentFile { get; set; }
    public int RestartCount { get; set; }
    public DateTimeOffset LastStartTime { get; set; }
    public int ListeningPort { get; set; }
    public string RecentErrorsJson { get; set; } = "[]";
}
