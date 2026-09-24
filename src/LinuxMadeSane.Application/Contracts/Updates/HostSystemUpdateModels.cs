// Copyright (c) Linux Made Sane.
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

public enum HostReleaseUpgradeCheckState
{
    NotChecked = 0,
    ToolMissing = 1,
    Available = 2,
    NoRelease = 3,
    Disabled = 4,
    TemporarilyUnavailable = 5,
    Failed = 6,
    PublishedButUpgradeClosed = 7
}

public enum HostReleaseUpgradeChannel
{
    LtsOnly = 0,
    AllStableReleases = 1
}

public sealed record HostOsReleaseUpgradeInfo(
    bool ToolAvailable,
    bool IsAvailable,
    string Summary,
    string Detail,
    string? TargetRelease,
    bool CheckSucceeded = true,
    HostReleaseUpgradeCheckState CheckState = HostReleaseUpgradeCheckState.NotChecked,
    string UpgradeChannel = "unknown",
    string Diagnostic = "",
    DateTimeOffset? LastCheckedAtUtc = null,
    bool IsChecking = false);

public sealed record HostSystemUpdateJobStatus(
    HostSystemUpdateJobState State,
    string Summary,
    string Detail,
    int ProgressPercent,
    IReadOnlyList<string> LogLines,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool NeedsAttention = false);

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
