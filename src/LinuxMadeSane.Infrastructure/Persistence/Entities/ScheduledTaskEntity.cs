namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class ScheduledTaskEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int TaskKind { get; set; }
    public int ScheduleMode { get; set; }
    public int Minute { get; set; }
    public int Hour { get; set; }
    public int DayOfMonth { get; set; }
    public string DaysOfWeekCsv { get; set; } = string.Empty;
    public string CustomCronExpression { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string ScheduleSummary { get; set; } = string.Empty;
    public string RunAsUser { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public Guid? RunbookId { get; set; }
    public string ExecutionToken { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = string.Empty;
    public string ScriptArguments { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public bool CopyRecursive { get; set; }
    public bool CopyPreserveAttributes { get; set; }
    public bool CopyDeleteSourceAfterCopy { get; set; }
    public string MatchPatternsCsv { get; set; } = string.Empty;
    public bool MatchCaseInsensitive { get; set; }
    public int AgeFilterMode { get; set; }
    public int AgeFilterValue { get; set; }
    public int AgeFilterUnit { get; set; }
    public bool CleanupDeleteFiles { get; set; }
    public bool CleanupDeleteDirectories { get; set; }
    public bool UpdatePackageLists { get; set; }
    public bool UpgradeInstalledPackages { get; set; }
    public bool RemoveUnusedPackages { get; set; }
    public string CommandPreview { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
