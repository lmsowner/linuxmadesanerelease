// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Updates;

public enum HostPackageUpdateMode
{
    SecurityOnly = 0,
    AllPackages = 1,
    DistUpgrade = 2
}

public enum HostSystemUpdateJobState
{
    Idle = 0,
    Refreshing = 1,
    Applying = 2,
    Completed = 3,
    Failed = 4
}

public sealed record HostOsReleaseInfo(
    string PrettyName,
    string Id,
    string VersionId,
    string Codename);

public sealed record HostUpgradeablePackage(
    string Name,
    string CurrentVersion,
    string CandidateVersion,
    string Suite,
    bool IsSecurity);

public sealed record HostOsReleaseUpgradeInfo(
    bool ToolAvailable,
    bool IsAvailable,
    string Summary,
    string Detail,
    string? TargetRelease);

public sealed record HostSystemUpdateJobStatus(
    HostSystemUpdateJobState State,
    string Summary,
    string Detail,
    int ProgressPercent,
    IReadOnlyList<string> LogLines,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record HostUpdateScheduleSettings(
    bool Enabled,
    int HourLocal,
    int MinuteLocal,
    bool ApplyPackageUpdates,
    bool SecurityOnly,
    bool UseDistUpgrade,
    bool RebootIfRequired,
    DateTimeOffset? LastRunAtUtc,
    string? LastRunSummary,
    string? LastRunDayKey,
    DateTimeOffset UpdatedAtUtc);

public sealed record HostSystemUpdateSnapshot(
    HostOsReleaseInfo OperatingSystem,
    int UpgradeableCount,
    int SecurityCount,
    bool RebootRequired,
    IReadOnlyList<string> RebootRequiredPackages,
    IReadOnlyList<HostUpgradeablePackage> Packages,
    HostOsReleaseUpgradeInfo ReleaseUpgrade,
    DateTimeOffset? LastRefreshedAtUtc,
    HostSystemUpdateJobStatus Job,
    HostUpdateScheduleSettings Schedule);
