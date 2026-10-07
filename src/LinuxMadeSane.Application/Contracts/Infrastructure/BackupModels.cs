// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Infrastructure;

public sealed class BackupRepository
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Path { get; set; } = "";
    public string PasswordReference { get; set; } = "";
    public string MountPath { get; set; } = "";
    public string MountSource { get; set; } = "";
}
public sealed class BackupSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RepositoryId { get; set; }
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public bool IncludeLms { get; set; } = true;
    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 6;
    public Guid? ScheduleId { get; set; }
    public bool ScheduleEnabled { get; set; }
    public bool ScheduleUsesDaily { get; set; } = true;
    public LinuxMadeSane.Core.Enums.ScheduledTaskScheduleMode ScheduleMode { get; set; } = LinuxMadeSane.Core.Enums.ScheduledTaskScheduleMode.Daily;
    public string ScheduleDaysOfWeekCsv { get; set; } = "0";
    public int ScheduleDayOfMonth { get; set; } = 1;
    public int ScheduleHour { get; set; } = 2;
    public int ScheduleMinute { get; set; }
    public string ScheduleSummary { get; set; } = "No schedule";
    public DateTimeOffset? LastFinishedUtc { get; set; }
    public bool? LastRunSucceeded { get; set; }
    public DateTimeOffset? LastSuccessfulBackupUtc { get; set; }
}
public sealed record BackupOperation(Guid Id, Guid RepositoryId, Guid? SetId, string Kind,
    DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, bool Success, string Detail);
public sealed record BackupWorkspace(IReadOnlyList<BackupRepository> Repositories, IReadOnlyList<BackupSet> Sets,
    IReadOnlyList<BackupOperation> History, IReadOnlyList<string> Destinations)
{
    public IReadOnlyList<BackupNetworkDestination> NetworkDestinations { get; init; } = [];
}
public sealed record BackupNetworkDestination(string Path, string Share, bool IsMounted, bool IsReadOnly, Guid? ManagedMountId);
public sealed record BackupSnapshot(string Id, DateTimeOffset Time, IReadOnlyList<string> Paths)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}
public sealed record BackupFile(string Path, string Type, long Size);
