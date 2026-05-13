namespace LinuxMadeSane.Web.Services;

public enum ApplicationUpdateState
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Installing,
    Completed,
    Failed
}

public sealed record ApplicationUpdateStatus(
    ApplicationUpdateState State,
    string Summary,
    string Detail,
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string Edition,
    string Rid,
    string ManifestUrl,
    string InstallScriptUrl,
    bool ScheduledChecksEnabled,
    bool InstallAutomatically,
    int CheckIntervalMinutes,
    int ProgressPercent,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? LastInstallStartedAtUtc,
    DateTimeOffset? LastInstallCompletedAtUtc,
    IReadOnlyList<string> LogLines);
