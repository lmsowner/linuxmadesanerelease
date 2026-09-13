// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Updates;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed partial class LinuxHostSystemUpdateService(
    ILinuxCommandRunner commandRunner,
    ILocalSystemMaintenanceService maintenanceService,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<LinuxHostSystemUpdateService> logger) : IHostSystemUpdateService
{
    private const int MaxLogLines = 200;
    private const string FailureStatusDetail = "See the Host update log below for details.";
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan ReleaseCheckTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReleaseUpgradeTimeout = TimeSpan.FromHours(4);
    private static readonly string[] AptNetworkOptions =
    [
        "-o", "Acquire::http::Timeout=30",
        "-o", "Acquire::https::Timeout=30",
        "-o", "Acquire::Retries=0",
        "-o", "DPkg::Lock::Timeout=120"
    ];

    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object syncRoot = new();
    private HostSystemUpdateSnapshot snapshot = CreateEmptySnapshot();

    public HostSystemUpdateSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            return Clone(snapshot);
        }
    }

    public async Task<HostSystemUpdateSnapshot> RefreshAsync(
        bool refreshMetadata,
        CancellationToken cancellationToken = default)
    {
        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return GetSnapshot();
        }

        try
        {
            EnsureLinux();
            SetJob(
                HostSystemUpdateJobState.Refreshing,
                "Checking for updates…",
                "Reading package and release information.",
                10,
                clearPackages: true);
            AppendLog(refreshMetadata
                ? "Refreshing APT metadata and upgrade list."
                : "Refreshing upgrade list without a full metadata pull.");

            var aptNeedsAttention = false;
            if (refreshMetadata)
            {
                var update = await RunAptAsync(
                    [.. AptNetworkOptions, "update"],
                    MetadataTimeout,
                    "Refresh package metadata",
                    cancellationToken);
                if (update.ExitCode != 0)
                {
                    FailJob("Could not refresh package metadata.", BuildFailureDetail(update));
                    return GetSnapshot();
                }

                aptNeedsAttention = HasActionableAptDiagnostics(update);
                AppendLog(aptNeedsAttention
                    ? "Package metadata refreshed with warnings that need attention."
                    : "Package metadata refreshed.");
                SetProgress(35);
            }

            var packages = await ListUpgradeablePackagesAsync(cancellationToken);
            var release = await ReadReleaseUpgradeAsync(cancellationToken);
            var reboot = ReadRebootRequired();
            var schedule = await LoadScheduleAsync(cancellationToken);
            var os = ReadOsRelease();
            var now = timeProvider.GetUtcNow();

            CompleteRefresh(
                os,
                packages,
                reboot,
                release,
                schedule,
                now,
                summary: aptNeedsAttention ? "APT reported package source problems." : null,
                detail: aptNeedsAttention ? FailureStatusDetail : null,
                needsAttention: aptNeedsAttention);
            return GetSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Host system update refresh failed.");
            FailJob("Update check failed.", ex.Message);
            return GetSnapshot();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<HostSystemUpdateSnapshot> ApplyPackageUpdatesAsync(
        HostPackageUpdateMode mode,
        bool rebootIfRequired,
        CancellationToken cancellationToken = default)
    {
        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return GetSnapshot();
        }

        try
        {
            EnsureLinux();
            var started = timeProvider.GetUtcNow();
            var modeLabel = mode switch
            {
                HostPackageUpdateMode.SecurityOnly => "security updates",
                HostPackageUpdateMode.DistUpgrade => "dependency-aware upgrades",
                _ => "package updates"
            };
            SetJob(
                HostSystemUpdateJobState.Applying,
                $"Applying {modeLabel}…",
                "LMS is running a non-interactive APT upgrade.",
                5,
                started);
            AppendLog($"Starting {modeLabel}.");

            var update = await RunAptAsync(
                [.. AptNetworkOptions, "update"],
                MetadataTimeout,
                "Refresh package metadata",
                cancellationToken);
            if (update.ExitCode != 0)
            {
                FailJob("Could not refresh package metadata before applying updates.", BuildFailureDetail(update));
                return GetSnapshot();
            }
            var aptNeedsAttention = HasActionableAptDiagnostics(update);

            SetProgress(25);
            AppendLog("Metadata refreshed. Applying updates.");

            LinuxCommandResult apply;
            if (mode == HostPackageUpdateMode.SecurityOnly)
            {
                apply = await ApplySecurityUpdatesAsync(cancellationToken);
            }
            else
            {
                var verb = mode == HostPackageUpdateMode.DistUpgrade ? "dist-upgrade" : "upgrade";
                apply = await RunAptAsync(
                    [
                        .. AptNetworkOptions,
                        "-o", "Dpkg::Options::=--force-confold",
                        verb, "-y"
                    ],
                    ApplyTimeout,
                    $"Apply {verb}",
                    cancellationToken);
            }

            if (apply.ExitCode != 0)
            {
                FailJob("Package updates failed.", BuildFailureDetail(apply));
                return GetSnapshot();
            }
            aptNeedsAttention |= HasActionableAptDiagnostics(apply);

            AppendLog("Package updates completed. Rechecking status.");
            SetProgress(75);

            var packages = await ListUpgradeablePackagesAsync(cancellationToken);
            var release = await ReadReleaseUpgradeAsync(cancellationToken);
            var reboot = ReadRebootRequired();
            var schedule = await LoadScheduleAsync(cancellationToken);
            var os = ReadOsRelease();
            CompleteRefresh(
                os,
                packages,
                reboot,
                release,
                schedule,
                timeProvider.GetUtcNow(),
                HostSystemUpdateJobState.Completed,
                aptNeedsAttention
                    ? "Package updates completed with APT warnings."
                    : packages.Count == 0 ? "Packages are up to date." : $"{packages.Count} package(s) still pending.",
                aptNeedsAttention
                    ? FailureStatusDetail
                    : reboot.Required
                    ? "A reboot is required to finish applying kernel or core updates."
                    : "No reboot is currently required.",
                100,
                aptNeedsAttention);

            if (rebootIfRequired && reboot.Required)
            {
                AppendLog("Reboot requested after successful updates.");
                await maintenanceService.RebootAsync(cancellationToken);
            }

            return GetSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Host package update apply failed.");
            FailJob("Package updates failed.", ex.Message);
            return GetSnapshot();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<HostSystemUpdateSnapshot> RepairAptAsync(CancellationToken cancellationToken = default)
    {
        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return GetSnapshot();
        }

        try
        {
            EnsureLinux();
            SetJob(
                HostSystemUpdateJobState.Applying,
                "Repairing APT…",
                "Waiting for package locks, completing interrupted packages, and refreshing metadata.",
                5);
            AppendLog("Starting APT repair.");

            var configure = await RunPrivilegedAsync(
                "dpkg",
                ["--configure", "-a"],
                TimeSpan.FromMinutes(3),
                "Complete interrupted package configuration",
                cancellationToken);
            if (configure.ExitCode != 0)
            {
                FailJob("APT repair failed while completing package configuration.", BuildFailureDetail(configure));
                return GetSnapshot();
            }
            var aptNeedsAttention = HasActionableAptDiagnostics(configure);

            SetProgress(30);
            var fixBroken = await RunAptAsync(
                [.. AptNetworkOptions, "--fix-broken", "install", "-y"],
                ApplyTimeout,
                "Repair broken package dependencies",
                cancellationToken);
            if (fixBroken.ExitCode != 0)
            {
                FailJob("APT repair failed while fixing package dependencies.", BuildFailureDetail(fixBroken));
                return GetSnapshot();
            }
            aptNeedsAttention |= HasActionableAptDiagnostics(fixBroken);

            SetProgress(65);
            var metadata = await RunAptAsync(
                [.. AptNetworkOptions, "update"],
                MetadataTimeout,
                "Refresh package metadata after APT repair",
                cancellationToken);
            if (metadata.ExitCode != 0)
            {
                FailJob("APT repair completed partially but metadata refresh failed.", BuildFailureDetail(metadata));
                return GetSnapshot();
            }
            aptNeedsAttention |= HasActionableAptDiagnostics(metadata);

            var packages = await ListUpgradeablePackagesAsync(cancellationToken);
            var release = await ReadReleaseUpgradeAsync(cancellationToken);
            var reboot = ReadRebootRequired();
            var schedule = await LoadScheduleAsync(cancellationToken);
            CompleteRefresh(
                ReadOsRelease(),
                packages,
                reboot,
                release,
                schedule,
                timeProvider.GetUtcNow(),
                HostSystemUpdateJobState.Completed,
                aptNeedsAttention ? "APT repair completed with warnings." : "APT repair completed.",
                aptNeedsAttention
                    ? FailureStatusDetail
                    : packages.Count == 0 ? "Package metadata is current and no updates are pending." : $"{packages.Count} package(s) pending.",
                100,
                aptNeedsAttention);
            return GetSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "APT repair failed.");
            FailJob("APT repair failed.", ex.Message);
            return GetSnapshot();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<HostSystemUpdateSnapshot> StartReleaseUpgradeAsync(CancellationToken cancellationToken = default)
    {
        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return GetSnapshot();
        }

        try
        {
            EnsureLinux();
            var started = timeProvider.GetUtcNow();
            SetJob(
                HostSystemUpdateJobState.Applying,
                "Starting OS release upgrade…",
                "This can take a long time and may disconnect remote sessions.",
                5,
                started);
            AppendLog("Starting do-release-upgrade in non-interactive mode.");

            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "env",
                    [
                        "DEBIAN_FRONTEND=noninteractive",
                        "NEEDRESTART_MODE=a",
                        "do-release-upgrade",
                        "-f",
                        "DistUpgradeViewNonInteractive"
                    ],
                    RequiresSudo: true,
                    Timeout: ReleaseUpgradeTimeout,
                    Description: "Start OS release upgrade")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);

            if (result.ExitCode != 0)
            {
                FailJob("OS release upgrade did not complete.", BuildFailureDetail(result));
                return GetSnapshot();
            }

            AppendLog("Release upgrade command finished. Refreshing status.");
            var packages = await ListUpgradeablePackagesAsync(cancellationToken);
            var release = await ReadReleaseUpgradeAsync(cancellationToken);
            var reboot = ReadRebootRequired();
            var schedule = await LoadScheduleAsync(cancellationToken);
            CompleteRefresh(
                ReadOsRelease(),
                packages,
                reboot,
                release,
                schedule,
                timeProvider.GetUtcNow(),
                HostSystemUpdateJobState.Completed,
                "OS release upgrade finished.",
                reboot.Required
                    ? "Reboot to finish the release upgrade."
                    : "Verify services after the upgrade.",
                100);
            return GetSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OS release upgrade failed.");
            FailJob("OS release upgrade failed.", ex.Message);
            return GetSnapshot();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public Task<HostUpdateScheduleSettings> GetScheduleAsync(CancellationToken cancellationToken = default) =>
        LoadScheduleAsync(cancellationToken);

    public async Task<HostUpdateScheduleSettings> SaveScheduleAsync(
        HostUpdateScheduleSettings settings,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostUpdateScheduleStore>();
        var saved = await store.SaveAsync(
            settings with { UpdatedAtUtc = timeProvider.GetUtcNow() },
            cancellationToken);
        lock (syncRoot)
        {
            snapshot = snapshot with { Schedule = saved };
        }

        return saved;
    }

    public Task RebootAsync(CancellationToken cancellationToken = default) =>
        maintenanceService.RebootAsync(cancellationToken);

    internal static IReadOnlyList<HostUpgradeablePackage> ParseUpgradeablePackages(string output)
    {
        var packages = new List<HostUpgradeablePackage>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("Listing", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = UpgradeablePackageRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var suite = match.Groups["suite"].Value;
            packages.Add(new HostUpgradeablePackage(
                match.Groups["name"].Value,
                match.Groups["current"].Value,
                match.Groups["candidate"].Value,
                suite,
                suite.Contains("security", StringComparison.OrdinalIgnoreCase)));
        }

        return packages
            .OrderByDescending(item => item.IsSecurity)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static HostOsReleaseUpgradeInfo ParseReleaseUpgradeCheck(string output, string error, bool toolAvailable)
    {
        if (!toolAvailable)
        {
            return new HostOsReleaseUpgradeInfo(
                false,
                false,
                "Release upgrade tool not installed",
                "Install ubuntu-release-upgrader-core (or your distro equivalent) to check for a new OS release.",
                null);
        }

        var text = $"{output}\n{error}";
        var match = NewReleaseRegex().Match(text);
        if (match.Success)
        {
            var target = match.Groups["release"].Value;
            return new HostOsReleaseUpgradeInfo(
                true,
                true,
                $"New OS release {target} is available",
                "Release upgrades change the OS version. Back up first, prefer a maintenance window, and expect a reboot.",
                target);
        }

        if (text.Contains("No new release found", StringComparison.OrdinalIgnoreCase))
        {
            return new HostOsReleaseUpgradeInfo(
                true,
                false,
                "No newer OS release available",
                "This host is on the latest supported release path reported by do-release-upgrade.",
                null);
        }

        return new HostOsReleaseUpgradeInfo(
            true,
            false,
            "Could not determine release upgrade status",
            string.IsNullOrWhiteSpace(text) ? "do-release-upgrade returned no usable output." : text.Trim(),
            null);
    }

    private async Task<LinuxCommandResult> ApplySecurityUpdatesAsync(CancellationToken cancellationToken)
    {
        var packages = await ListUpgradeablePackagesAsync(cancellationToken);
        var securityNames = packages
            .Where(item => item.IsSecurity)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (securityNames.Length == 0)
        {
            AppendLog("No security packages are pending.");
            return new LinuxCommandResult(
                "security-filter",
                0,
                "No security packages pending.",
                string.Empty,
                timeProvider.GetUtcNow(),
                timeProvider.GetUtcNow(),
                false);
        }

        AppendLog($"Installing {securityNames.Length} security package(s).");
        return await RunAptAsync(
            [
                .. AptNetworkOptions,
                "-o", "Dpkg::Options::=--force-confold",
                "install", "-y",
                .. securityNames
            ],
            ApplyTimeout,
            "Apply security updates",
            cancellationToken);
    }

    private async Task<IReadOnlyList<HostUpgradeablePackage>> ListUpgradeablePackagesAsync(
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "apt",
                ["list", "--upgradable", "-qq"],
                RequiresSudo: false,
                Timeout: TimeSpan.FromMinutes(2),
                Description: "List upgradable packages"),
            dryRun: false,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not list upgradable packages: {BuildFailureDetail(result)}");
        }

        return ParseUpgradeablePackages(result.StandardOutput);
    }

    private async Task<HostOsReleaseUpgradeInfo> ReadReleaseUpgradeAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "do-release-upgrade",
                ["-c"],
                RequiresSudo: false,
                Timeout: ReleaseCheckTimeout,
                Description: "Check for OS release upgrade")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);

        var toolMissing = result.ExitCode == 127 ||
                          result.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                          result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase);
        return ParseReleaseUpgradeCheck(result.StandardOutput, result.StandardError, !toolMissing || result.ExitCode == 0);
    }

    private async Task<LinuxCommandResult> RunAptAsync(
        IReadOnlyList<string> aptArguments,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        var request = new LinuxCommandRequest(
            "env",
            ["DEBIAN_FRONTEND=noninteractive", "NEEDRESTART_MODE=a", "apt-get", .. aptArguments],
            RequiresSudo: true,
            Timeout: timeout,
            Description: description);
        var result = commandRunner is IStreamingLinuxCommandRunner streamingRunner
            ? await streamingRunner.RunStreamingAsync(request, false, output => AppendLog(output.Text), cancellationToken)
            : await commandRunner.RunAsync(request, false, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            if (commandRunner is not IStreamingLinuxCommandRunner)
            {
                AppendLog(TrimForLog(result.StandardOutput));
            }
        }

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            if (commandRunner is not IStreamingLinuxCommandRunner)
            {
                AppendLog(TrimForLog(result.StandardError));
            }
        }

        return result;
    }

    private async Task<LinuxCommandResult> RunPrivilegedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        var request = new LinuxCommandRequest(
            "env",
            ["DEBIAN_FRONTEND=noninteractive", "NEEDRESTART_MODE=a", fileName, .. arguments],
            RequiresSudo: true,
            Timeout: timeout,
            Description: description);
        var result = commandRunner is IStreamingLinuxCommandRunner streamingRunner
            ? await streamingRunner.RunStreamingAsync(request, false, output => AppendLog(output.Text), cancellationToken)
            : await commandRunner.RunAsync(request, false, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            if (commandRunner is not IStreamingLinuxCommandRunner)
            {
                AppendLog(TrimForLog(result.StandardOutput));
            }
        }

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            if (commandRunner is not IStreamingLinuxCommandRunner)
            {
                AppendLog(TrimForLog(result.StandardError));
            }
        }

        return result;
    }

    private async Task<HostUpdateScheduleSettings> LoadScheduleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostUpdateScheduleStore>();
        return await store.GetAsync(cancellationToken);
    }

    private void CompleteRefresh(
        HostOsReleaseInfo os,
        IReadOnlyList<HostUpgradeablePackage> packages,
        (bool Required, IReadOnlyList<string> Packages) reboot,
        HostOsReleaseUpgradeInfo release,
        HostUpdateScheduleSettings schedule,
        DateTimeOffset refreshedAtUtc,
        HostSystemUpdateJobState state = HostSystemUpdateJobState.Completed,
        string? summary = null,
        string? detail = null,
        int progressPercent = 100,
        bool needsAttention = false)
    {
        var securityCount = packages.Count(item => item.IsSecurity);
        lock (syncRoot)
        {
            var job = snapshot.Job with
            {
                State = state,
                Summary = summary ?? (packages.Count == 0
                    ? "Host packages are current"
                    : $"{packages.Count} package update(s) available"),
                Detail = detail ?? (reboot.Required
                    ? "A reboot is already pending on this host."
                    : securityCount > 0
                        ? $"{securityCount} security update(s) included."
                        : "Review package updates, OS upgrades, or schedule automatic maintenance."),
                ProgressPercent = progressPercent,
                CompletedAtUtc = refreshedAtUtc,
                LogLines = snapshot.Job.LogLines.ToArray(),
                NeedsAttention = needsAttention
            };
            snapshot = new HostSystemUpdateSnapshot(
                os,
                packages.Count,
                securityCount,
                reboot.Required,
                reboot.Packages,
                packages,
                release,
                refreshedAtUtc,
                job,
                schedule);
        }
    }

    private void SetJob(
        HostSystemUpdateJobState state,
        string summary,
        string detail,
        int progressPercent,
        DateTimeOffset? startedAtUtc = null,
        bool clearPackages = false)
    {
        lock (syncRoot)
        {
            var updatedSnapshot = snapshot with
            {
                Job = snapshot.Job with
                {
                    State = state,
                    Summary = summary,
                    Detail = detail,
                    ProgressPercent = progressPercent,
                    StartedAtUtc = startedAtUtc ?? snapshot.Job.StartedAtUtc ?? timeProvider.GetUtcNow(),
                    CompletedAtUtc = state is HostSystemUpdateJobState.Completed or HostSystemUpdateJobState.Failed
                        ? timeProvider.GetUtcNow()
                        : null,
                    NeedsAttention = state == HostSystemUpdateJobState.Failed
                }
            };

            snapshot = clearPackages
                ? updatedSnapshot with
                {
                    UpgradeableCount = 0,
                    SecurityCount = 0,
                    Packages = Array.Empty<HostUpgradeablePackage>(),
                    LastRefreshedAtUtc = null
                }
                : updatedSnapshot;
        }
    }

    private void SetProgress(int progressPercent)
    {
        lock (syncRoot)
        {
            snapshot = snapshot with
            {
                Job = snapshot.Job with { ProgressPercent = Math.Clamp(progressPercent, 0, 100) }
            };
        }
    }

    private void FailJob(string summary, string detail)
    {
        AppendLog(detail);
        SetJob(HostSystemUpdateJobState.Failed, summary, FailureStatusDetail, 100);
    }

    internal static bool HasActionableAptDiagnostics(LinuxCommandResult result)
    {
        var output = $"{result.StandardOutput}\n{result.StandardError}";
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line =>
                line.StartsWith("W:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("E:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Err:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("configured multiple times", StringComparison.OrdinalIgnoreCase));
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (syncRoot)
        {
            var lines = snapshot.Job.LogLines.ToList();
            foreach (var part in line.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                lines.Add($"[{timeProvider.GetUtcNow():HH:mm:ss}] {part}");
            }

            if (lines.Count > MaxLogLines)
            {
                lines = lines.TakeLast(MaxLogLines).ToList();
            }

            snapshot = snapshot with
            {
                Job = snapshot.Job with { LogLines = lines }
            };
        }
    }

    private static HostSystemUpdateSnapshot CreateEmptySnapshot() =>
        new(
            new HostOsReleaseInfo("Unknown", "unknown", string.Empty, string.Empty),
            0,
            0,
            false,
            Array.Empty<string>(),
            Array.Empty<HostUpgradeablePackage>(),
            new HostOsReleaseUpgradeInfo(false, false, "Not checked yet", "Refresh to inspect package and OS updates.", null),
            null,
            new HostSystemUpdateJobStatus(
                HostSystemUpdateJobState.Idle,
                "Ready",
                "Check for package updates, apply them from LMS, or schedule automatic maintenance.",
                0,
                Array.Empty<string>(),
                null,
                null),
            new HostUpdateScheduleSettings(
                false,
                3,
                0,
                true,
                false,
                false,
                false,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch));

    private static HostSystemUpdateSnapshot Clone(HostSystemUpdateSnapshot source) =>
        source with
        {
            Packages = source.Packages.ToArray(),
            RebootRequiredPackages = source.RebootRequiredPackages.ToArray(),
            Job = source.Job with { LogLines = source.Job.LogLines.ToArray() }
        };

    private static HostOsReleaseInfo ReadOsRelease()
    {
        try
        {
            if (!File.Exists("/etc/os-release"))
            {
                return new HostOsReleaseInfo(Environment.OSVersion.ToString(), "linux", string.Empty, string.Empty);
            }

            var values = File.ReadAllLines("/etc/os-release")
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(
                    parts => parts[0].Trim(),
                    parts => parts[1].Trim().Trim('"'),
                    StringComparer.OrdinalIgnoreCase);
            return new HostOsReleaseInfo(
                values.GetValueOrDefault("PRETTY_NAME") ?? "Linux",
                values.GetValueOrDefault("ID") ?? "linux",
                values.GetValueOrDefault("VERSION_ID") ?? string.Empty,
                values.GetValueOrDefault("VERSION_CODENAME") ?? string.Empty);
        }
        catch
        {
            return new HostOsReleaseInfo(Environment.OSVersion.ToString(), "linux", string.Empty, string.Empty);
        }
    }

    private static (bool Required, IReadOnlyList<string> Packages) ReadRebootRequired()
    {
        var required = File.Exists("/var/run/reboot-required");
        if (!required)
        {
            return (false, Array.Empty<string>());
        }

        try
        {
            if (!File.Exists("/var/run/reboot-required.pkgs"))
            {
                return (true, Array.Empty<string>());
            }

            var packages = File.ReadAllLines("/var/run/reboot-required.pkgs")
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return (true, packages);
        }
        catch
        {
            return (true, Array.Empty<string>());
        }
    }

    private static string BuildFailureDetail(LinuxCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return TrimForLog(result.StandardError);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return TrimForLog(result.StandardOutput);
        }

        return $"Exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}.";
    }

    private static string TrimForLog(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 1_500 ? trimmed : trimmed[..1_500] + "…";
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("Host package updates are only available on Linux.");
        }
    }

    [GeneratedRegex(
        @"^(?<name>[^/\s]+)/(?<suite>\S+)\s+(?<candidate>\S+)\s+\S+\s+\[upgradable from:\s*(?<current>[^\]]+)\]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeablePackageRegex();

    [GeneratedRegex(
        @"New release ['""]?(?<release>[^'""\s]+)['""]? available",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NewReleaseRegex();
}
