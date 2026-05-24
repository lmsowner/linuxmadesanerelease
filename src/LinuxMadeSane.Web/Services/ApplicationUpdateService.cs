// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;
using System.Text.Json;
using LinuxMadeSane.Core.Versioning;
using Microsoft.Extensions.Options;

namespace LinuxMadeSane.Web.Services;

public sealed class ApplicationUpdateService(
    HttpClient httpClient,
    IOptionsMonitor<ApplicationUpdateOptions> optionsMonitor,
    ILogger<ApplicationUpdateService> logger)
{
    private const int MaxLogLines = 180;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly object syncRoot = new();
    private ApplicationUpdateStatus status = BuildInitialStatus(optionsMonitor.CurrentValue);

    public ApplicationUpdateStatus GetStatus()
    {
        lock (syncRoot)
        {
            return status with { LogLines = status.LogLines.ToArray() };
        }
    }

    public async Task<ApplicationUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return GetStatus();
        }

        try
        {
            return await CheckForUpdatesCoreAsync(cancellationToken);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<ApplicationUpdateStatus> InstallLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!await operationLock.WaitAsync(0, cancellationToken))
        {
            return GetStatus();
        }

        try
        {
            var options = optionsMonitor.CurrentValue;
            var edition = NormalizeEdition(options.Edition);
            if (!edition.Equals("community", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(options.UpdateCommand))
            {
                SetStatus(current => current with
                {
                    State = ApplicationUpdateState.Failed,
                    Summary = "Protected updates need a configured update command.",
                    Detail = "The built-in public installer is for Community Edition. Configure ApplicationUpdates:UpdateCommand before enabling Pro or Enterprise self-update.",
                    ProgressPercent = 0
                });
                AppendLog("Update install refused because this edition does not have a protected update command configured.");
                return GetStatus();
            }

            var check = await CheckForUpdatesCoreAsync(cancellationToken);
            if (!check.IsUpdateAvailable)
            {
                return check;
            }

            var startedAtUtc = DateTimeOffset.UtcNow;
            SetStatus(current => current with
            {
                State = ApplicationUpdateState.Installing,
                Summary = $"Installing Linux Made Sane {current.LatestVersion}.",
                Detail = "The updater is downloading the latest public release and applying it in place.",
                ProgressPercent = 5,
                LastInstallStartedAtUtc = startedAtUtc,
                LastInstallCompletedAtUtc = null
            });
            AppendLog($"Starting self-update from {check.CurrentVersion} to {check.LatestVersion}.");

            var updateCommand = BuildUpdateCommand(options);
            AppendLog(string.IsNullOrWhiteSpace(options.UpdateCommand)
                ? updateCommand.RunsDetached
                    ? "Handing the update to the detached systemd self-update helper."
                    : "Running the installed update helper or public Community installer."
                : "Running the configured update command.");
            var exitCode = await RunUpdateCommandAsync(updateCommand.ShellCommand, options, cancellationToken);
            var completedAtUtc = DateTimeOffset.UtcNow;

            if (exitCode == 0)
            {
                if (updateCommand.RunsDetached)
                {
                    SetStatus(current => current with
                    {
                        State = ApplicationUpdateState.Installing,
                        Summary = $"Linux Made Sane {current.LatestVersion} update started.",
                        Detail = "The update is running outside the LMS web process. The service will restart when the installer reaches the service phase.",
                        ProgressPercent = Math.Max(current.ProgressPercent, 20),
                        LastInstallCompletedAtUtc = null
                    });
                    AppendLog("Detached self-update was queued successfully. LMS should restart during the install.");
                    return GetStatus();
                }

                SetStatus(current => current with
                {
                    State = ApplicationUpdateState.Completed,
                    Summary = $"Linux Made Sane {current.LatestVersion} installed.",
                    Detail = "The service may restart as part of the installer. Refresh the UI if the connection drops during the update.",
                    ProgressPercent = 100,
                    IsUpdateAvailable = false,
                    LastInstallCompletedAtUtc = completedAtUtc
                });
                AppendLog("Self-update completed successfully.");
            }
            else
            {
                SetStatus(current => current with
                {
                    State = ApplicationUpdateState.Failed,
                    Summary = $"Update failed with exit code {exitCode}.",
                    Detail = "Review the updater log. The existing service remains in place unless the installer reached its restart phase.",
                    ProgressPercent = Math.Max(current.ProgressPercent, 95),
                    LastInstallCompletedAtUtc = completedAtUtc
                });
                AppendLog($"Self-update failed with exit code {exitCode}.");
            }

            return GetStatus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(current => current with
            {
                State = ApplicationUpdateState.Failed,
                Summary = "Update cancelled.",
                Detail = "The update operation was cancelled before it completed."
            });
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Linux Made Sane self-update failed.");
            SetStatus(current => current with
            {
                State = ApplicationUpdateState.Failed,
                Summary = "Update failed.",
                Detail = ex.Message,
                ProgressPercent = Math.Max(current.ProgressPercent, 95),
                LastInstallCompletedAtUtc = DateTimeOffset.UtcNow
            });
            AppendLog($"Update failed: {ex.Message}");
            return GetStatus();
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<ApplicationUpdateStatus> CheckForUpdatesCoreAsync(CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        var manifestUrl = NormalizeAbsoluteUrl(options.ManifestUrl, "https://www.linuxmadesane.com/api/downloads/manifest");
        var installScriptUrl = NormalizeAbsoluteUrl(options.InstallScriptUrl, "https://www.linuxmadesane.com/install.sh");
        var edition = NormalizeEdition(options.Edition);
        var rid = NormalizeRid(options.Rid);
        var currentVersion = LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly);

        SetStatus(current => current with
        {
            State = ApplicationUpdateState.Checking,
            Summary = "Checking for Linux Made Sane updates.",
            Detail = manifestUrl,
            CurrentVersion = currentVersion,
            Edition = edition,
            Rid = rid,
            ManifestUrl = manifestUrl,
            InstallScriptUrl = installScriptUrl,
            ScheduledChecksEnabled = options.Enabled,
            InstallAutomatically = options.InstallAutomatically,
            CheckIntervalMinutes = NormalizeIntervalMinutes(options.CheckIntervalMinutes),
            ProgressPercent = 15
        });
        AppendLog($"Checking release manifest: {manifestUrl}");

        try
        {
            using var response = await httpClient.GetAsync(manifestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<ReleaseManifestDto>(stream, JsonOptions, cancellationToken);
            if (manifest is null)
            {
                throw new InvalidOperationException("The release manifest was empty.");
            }

            var latestVersion = ResolveLatestVersion(manifest, edition);
            var matchingAsset = ResolveMatchingAsset(manifest, edition, latestVersion, rid);
            var isAvailable = ApplicationUpdateVersionComparer.IsNewer(latestVersion, currentVersion);
            var checkedAtUtc = DateTimeOffset.UtcNow;

            SetStatus(current => current with
            {
                State = isAvailable ? ApplicationUpdateState.UpdateAvailable : ApplicationUpdateState.UpToDate,
                Summary = isAvailable
                    ? $"Linux Made Sane {latestVersion} is available."
                    : $"Linux Made Sane is current at {currentVersion}.",
                Detail = matchingAsset is null
                    ? $"No {edition} {rid} tarball is listed in the release manifest yet."
                    : $"{matchingAsset.FileName} · {FormatBytes(matchingAsset.SizeBytes)} · sha256 {matchingAsset.Sha256}",
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                IsUpdateAvailable = isAvailable && matchingAsset is not null,
                LastCheckedAtUtc = checkedAtUtc,
                ProgressPercent = 100
            });

            AppendLog(matchingAsset is null
                ? $"Manifest checked. Latest {edition} version is {latestVersion}, but no {rid} asset was found."
                : $"Manifest checked. Latest {edition} {rid} asset is {matchingAsset.FileName}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Linux Made Sane update check failed.");
            SetStatus(current => current with
            {
                State = ApplicationUpdateState.Failed,
                Summary = "Update check failed.",
                Detail = ex.Message,
                LastCheckedAtUtc = DateTimeOffset.UtcNow,
                ProgressPercent = 100
            });
            AppendLog($"Update check failed: {ex.Message}");
        }

        return GetStatus();
    }

    private async Task<int> RunUpdateCommandAsync(
        string commandText,
        ApplicationUpdateOptions options,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellationTokenSource = new CancellationTokenSource(
            TimeSpan.FromMinutes(Math.Clamp(options.InstallTimeoutMinutes, 5, 180)));
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellationTokenSource.Token);
        var linkedToken = linkedCancellationTokenSource.Token;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-lc");
        process.StartInfo.ArgumentList.Add(commandText);
        process.OutputDataReceived += (_, args) => HandleProcessOutput(args.Data, false);
        process.ErrorDataReceived += (_, args) => HandleProcessOutput(args.Data, true);

        if (!process.Start())
        {
            throw new InvalidOperationException("The updater process did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(linkedToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested &&
                                                !cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            AppendLog("Updater timed out and was stopped.");
            return 124;
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private void HandleProcessOutput(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.TrimEnd();
        AppendLog(isError ? $"stderr: {trimmed}" : trimmed);
        var progress = EstimateInstallProgress(trimmed);
        if (progress.HasValue)
        {
            SetStatus(current => current with
            {
                ProgressPercent = Math.Max(current.ProgressPercent, progress.Value),
                Detail = trimmed
            });
        }
    }

    private static int? EstimateInstallProgress(string line)
    {
        if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
        {
            return 25;
        }

        if (line.Contains("unpack", StringComparison.OrdinalIgnoreCase))
        {
            return 45;
        }

        if (line.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            return 65;
        }

        if (line.Contains("Starting", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("restart", StringComparison.OrdinalIgnoreCase))
        {
            return 85;
        }

        if (line.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("installed", StringComparison.OrdinalIgnoreCase))
        {
            return 98;
        }

        return null;
    }

    private ApplicationUpdateCommand BuildUpdateCommand(ApplicationUpdateOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.UpdateCommand))
        {
            return new ApplicationUpdateCommand(options.UpdateCommand.Trim(), false);
        }

        var helperPath = string.IsNullOrWhiteSpace(options.UpdateHelperPath)
            ? "/usr/local/sbin/linux-made-sane-update"
            : options.UpdateHelperPath.Trim();
        var isRoot = OperatingSystem.IsLinux() && Environment.UserName.Equals("root", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux() && File.Exists(helperPath))
        {
            var helperCommand = $"{ShellQuote(helperPath)} --background";
            return new ApplicationUpdateCommand(
                isRoot ? helperCommand : $"sudo -n {helperCommand}",
                true);
        }

        var installScriptUrl = NormalizeAbsoluteUrl(options.InstallScriptUrl, "https://www.linuxmadesane.com/install.sh");
        if (OperatingSystem.IsLinux() &&
            Directory.Exists("/run/systemd/system") &&
            (File.Exists("/usr/bin/systemd-run") || File.Exists("/bin/systemd-run")))
        {
            var sudoPrefix = isRoot ? string.Empty : "sudo -n ";
            var detachedInstallCommand = BuildPublicInstallerFallbackScript(installScriptUrl, runsAsRoot: true);
            var unitName = $"linux-made-sane-self-update-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            return new ApplicationUpdateCommand(
                $"{sudoPrefix}systemd-run --unit={ShellQuote(unitName)} --collect --property=Type=exec bash -lc {ShellQuote(detachedInstallCommand)}",
                true);
        }

        return new ApplicationUpdateCommand(BuildPublicInstallerFallbackScript(installScriptUrl, isRoot), false);
    }

    private static string BuildPublicInstallerFallbackScript(string installScriptUrl, bool runsAsRoot)
    {
        var sudoPrefix = runsAsRoot ? string.Empty : "sudo -n ";
        return string.Join(
            "\n",
            "set -euo pipefail",
            "SERVICE_UNIT='linux-made-sane.service'",
            "CURRENT_DIR='/opt/linuxmadesane/ce/current'",
            "PREVIOUS_CURRENT_TARGET=''",
            "SERVICE_WAS_ACTIVE=false",
            "if [[ -e \"$CURRENT_DIR\" || -L \"$CURRENT_DIR\" ]]; then",
            "  PREVIOUS_CURRENT_TARGET=\"$(readlink -f \"$CURRENT_DIR\" 2>/dev/null || true)\"",
            "fi",
            $"if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]] && {sudoPrefix}systemctl is-active --quiet \"$SERVICE_UNIT\"; then",
            "  SERVICE_WAS_ACTIVE=true",
            "fi",
            "rollback_self_update() {",
            "  local reason=\"$1\"",
            "  echo \"Linux Made Sane self-update failed: $reason\" >&2",
            "  if [[ -n \"$PREVIOUS_CURRENT_TARGET\" && -d \"$PREVIOUS_CURRENT_TARGET\" ]]; then",
            "    echo \"Rolling back to $PREVIOUS_CURRENT_TARGET\" >&2",
            $"    {sudoPrefix}ln -sfn \"$PREVIOUS_CURRENT_TARGET\" \"$CURRENT_DIR\" || true",
            "  fi",
            "  if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]] && { [[ \"$SERVICE_WAS_ACTIVE\" == \"true\" ]] || [[ -n \"$PREVIOUS_CURRENT_TARGET\" ]]; }; then",
            $"    {sudoPrefix}systemctl daemon-reload >/dev/null 2>&1 || true",
            $"    {sudoPrefix}systemctl restart \"$SERVICE_UNIT\" >/dev/null 2>&1 || true",
            "  fi",
            "}",
            "verify_self_update_active() {",
            "  command -v systemctl >/dev/null 2>&1 || return 0",
            "  [[ -d /run/systemd/system ]] || return 0",
            "  for _ in $(seq 1 30); do",
            $"    if {sudoPrefix}systemctl is-active --quiet \"$SERVICE_UNIT\"; then",
            "      return 0",
            "    fi",
            "    sleep 1",
            "  done",
            $"  {sudoPrefix}systemctl --no-pager --full status \"$SERVICE_UNIT\" >&2 || true",
            "  return 1",
            "}",
            $"if ! curl -fsSL {ShellQuote(installScriptUrl)} | {sudoPrefix}env LMS_SOURCE=lms-auto-update bash -s -- --install; then",
            "  rollback_self_update 'installer returned a non-zero exit code'",
            "  exit 1",
            "fi",
            "if ! verify_self_update_active; then",
            "  rollback_self_update 'service did not become active after install'",
            "  exit 1",
            "fi");
    }

    private void SetStatus(Func<ApplicationUpdateStatus, ApplicationUpdateStatus> update)
    {
        lock (syncRoot)
        {
            status = update(status);
        }
    }

    private void AppendLog(string message)
    {
        lock (syncRoot)
        {
            var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
            status = status with
            {
                LogLines = status.LogLines
                    .Append(line)
                    .TakeLast(MaxLogLines)
                    .ToArray()
            };
        }
    }

    private static ApplicationUpdateStatus BuildInitialStatus(ApplicationUpdateOptions options)
    {
        var currentVersion = LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly);
        return new ApplicationUpdateStatus(
            ApplicationUpdateState.Idle,
            "Update status has not been checked yet.",
            "Run a manual check or wait for the scheduled update check.",
            currentVersion,
            "unknown",
            false,
            NormalizeEdition(options.Edition),
            NormalizeRid(options.Rid),
            NormalizeAbsoluteUrl(options.ManifestUrl, "https://www.linuxmadesane.com/api/downloads/manifest"),
            NormalizeAbsoluteUrl(options.InstallScriptUrl, "https://www.linuxmadesane.com/install.sh"),
            options.Enabled,
            options.InstallAutomatically,
            NormalizeIntervalMinutes(options.CheckIntervalMinutes),
            0,
            null,
            null,
            null,
            []);
    }

    private static string ResolveLatestVersion(ReleaseManifestDto manifest, string edition)
    {
        var latest = edition.Equals("pro", StringComparison.OrdinalIgnoreCase)
            ? manifest.LatestProVersion
            : manifest.LatestCommunityVersion;

        return string.IsNullOrWhiteSpace(latest) ? manifest.LatestVersion : latest.Trim();
    }

    private static ReleaseAssetDto? ResolveMatchingAsset(
        ReleaseManifestDto manifest,
        string edition,
        string latestVersion,
        string rid)
    {
        var releases = edition.Equals("pro", StringComparison.OrdinalIgnoreCase)
            ? manifest.Pro
            : manifest.Community;

        return releases
            .Where(asset => asset.Version.Equals(latestVersion, StringComparison.OrdinalIgnoreCase))
            .Where(asset => asset.Rid.Equals(rid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.PublishedAtUtc)
            .FirstOrDefault();
    }

    private static string NormalizeEdition(string? edition) =>
        string.Equals(edition?.Trim(), "pro", StringComparison.OrdinalIgnoreCase)
            ? "pro"
            : "community";

    private static string NormalizeRid(string? rid) =>
        string.IsNullOrWhiteSpace(rid) ? "linux-x64" : rid.Trim();

    private static string NormalizeAbsoluteUrl(string? value, string fallback)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
        {
            return uri.ToString();
        }

        return fallback;
    }

    private static int NormalizeIntervalMinutes(int value) => Math.Clamp(value, 15, 10_080);

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed record ApplicationUpdateCommand(string ShellCommand, bool RunsDetached);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private sealed record ReleaseManifestDto(
        string LatestVersion,
        string LatestCommunityVersion,
        string LatestProVersion,
        IReadOnlyList<ReleaseAssetDto> Community,
        IReadOnlyList<ReleaseAssetDto> Pro);

    private sealed record ReleaseAssetDto(
        string Edition,
        string Version,
        string Rid,
        string FileName,
        long SizeBytes,
        string Sha256,
        DateTimeOffset PublishedAtUtc);
}
