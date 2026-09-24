// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Diagnostics;

namespace LinuxMadeSane.Web.Services;

public sealed record DesktopHelperSetupStatus(
    bool IsManaged,
    bool IsEnabled,
    string Summary,
    string Detail);

public sealed record DesktopHelperSetupResult(
    bool Succeeded,
    DesktopHelperSetupStatus Status,
    string Message);

public sealed record GraphicalDesktopStatus(
    bool IsManaged,
    bool StartsOnBoot,
    bool DisplayManagerActive,
    string DefaultTarget,
    string DisplayManager,
    string Summary,
    string Detail);

public sealed record GraphicalDesktopResult(
    bool Succeeded,
    GraphicalDesktopStatus Status,
    string Message);

public sealed class DesktopHelperSetupService
{
    private const string DefaultManagerPath = "/usr/local/sbin/linux-made-sane-desktop-helper-setup";
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly ILogger<DesktopHelperSetupService> logger;
    private readonly string managerPath;
    private readonly bool runsAsRoot;

    public DesktopHelperSetupService(
        IConfiguration configuration,
        ILogger<DesktopHelperSetupService> logger)
        : this(
            configuration["DesktopHelperSetup:ManagerPath"],
            OperatingSystem.IsLinux() && Environment.UserName.Equals("root", StringComparison.OrdinalIgnoreCase),
            logger)
    {
    }

    internal DesktopHelperSetupService(
        string? managerPath,
        bool runsAsRoot,
        ILogger<DesktopHelperSetupService> logger)
    {
        this.managerPath = string.IsNullOrWhiteSpace(managerPath)
            ? DefaultManagerPath
            : managerPath.Trim();
        this.runsAsRoot = runsAsRoot;
        this.logger = logger;
    }

    public async Task<DesktopHelperSetupStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Unmanaged("Desktop Helper host setup is available on Linux installations only.");
        }

        if (!File.Exists(managerPath))
        {
            return Unmanaged("Install or update LMS before changing Desktop Helper from this page.");
        }

        var result = await RunManagerAsync("status", requiresRoot: false, TimeSpan.FromSeconds(10), cancellationToken);
        if (!result.Succeeded)
        {
            return new DesktopHelperSetupStatus(
                true,
                false,
                "Status unavailable",
                result.Message);
        }

        var enabled = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line.Equals("enabled", StringComparison.OrdinalIgnoreCase));
        return enabled
            ? new DesktopHelperSetupStatus(true, true, "Enabled", "Desktop Helper starts for graphical Linux sessions.")
            : new DesktopHelperSetupStatus(true, false, "Disabled", "This host remains headless; no desktop tray helper is started.");
    }

    public async Task<DesktopHelperSetupResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(managerPath))
            {
                var unavailable = await GetStatusAsync(cancellationToken);
                return new DesktopHelperSetupResult(false, unavailable, unavailable.Detail);
            }

            var action = enabled ? "enable" : "disable";
            var result = await RunManagerAsync(action, requiresRoot: true, TimeSpan.FromSeconds(60), cancellationToken);
            if (!result.Succeeded)
            {
                logger.LogWarning("Desktop Helper setup action {Action} failed: {Message}", action, result.Message);
                var current = await GetStatusAsync(cancellationToken);
                return new DesktopHelperSetupResult(false, current, result.Message);
            }

            var status = await GetStatusAsync(cancellationToken);
            var message = enabled
                ? "Desktop Helper enabled. An active desktop was started when available; otherwise it will start at the next graphical login."
                : "Desktop Helper disabled. LMS itself and its update service were left running.";
            return new DesktopHelperSetupResult(true, status, message);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<GraphicalDesktopStatus> GetGraphicalDesktopStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(managerPath))
        {
            return UnmanagedGraphicalDesktop(
                "Install or update LMS before changing graphical desktop startup from this page.");
        }

        var result = await RunManagerAsync(
            "desktop-status",
            requiresRoot: false,
            TimeSpan.FromSeconds(10),
            cancellationToken);
        if (!result.Succeeded)
        {
            return UnmanagedGraphicalDesktop(result.Message);
        }

        var values = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("managed", out var managedValue) ||
            !bool.TryParse(managedValue, out var isManaged) ||
            !isManaged)
        {
            return UnmanagedGraphicalDesktop("systemd host desktop controls are unavailable.");
        }

        var defaultTarget = values.GetValueOrDefault("default-target", "unknown");
        var displayManager = values.GetValueOrDefault("display-manager", "not installed");
        var startsOnBoot = defaultTarget.Equals("graphical.target", StringComparison.OrdinalIgnoreCase);
        var displayManagerActive = values.TryGetValue("display-manager-active", out var activeValue) &&
                                   bool.TryParse(activeValue, out var active) &&
                                   active;
        var summary = startsOnBoot
            ? displayManagerActive ? "Enabled and running" : "Enabled for boot"
            : displayManagerActive ? "Boot disabled; still running" : "Disabled";
        var detail = $"Default target: {defaultTarget}. Display manager {displayManager} is " +
                     (displayManagerActive ? "running." : "stopped.");
        return new GraphicalDesktopStatus(
            true,
            startsOnBoot,
            displayManagerActive,
            defaultTarget,
            displayManager,
            summary,
            detail);
    }

    public async Task<GraphicalDesktopResult> SetGraphicalDesktopEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(managerPath))
            {
                var unavailable = await GetGraphicalDesktopStatusAsync(cancellationToken);
                return new GraphicalDesktopResult(false, unavailable, unavailable.Detail);
            }

            var action = enabled ? "desktop-enable" : "desktop-disable";
            var result = await RunManagerAsync(action, requiresRoot: true, TimeSpan.FromSeconds(60), cancellationToken);
            if (!result.Succeeded)
            {
                logger.LogWarning("Graphical desktop setup action {Action} failed: {Message}", action, result.Message);
                var current = await GetGraphicalDesktopStatusAsync(cancellationToken);
                return new GraphicalDesktopResult(false, current, result.Message);
            }

            var status = await GetGraphicalDesktopStatusAsync(cancellationToken);
            var message = enabled
                ? "Graphical desktop enabled and selected for boot. Its display manager was started when installed."
                : "Graphical desktop and Desktop Helper disabled. The host now boots to multi-user mode, all LMS desktop-agent launch paths were disarmed, and the active display manager was stopped; LMS and SSH remain running.";
            return new GraphicalDesktopResult(true, status, message);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task<ManagerCommandResult> RunManagerAsync(
        string action,
        bool requiresRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = requiresRoot && !runsAsRoot ? "sudo" : managerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (requiresRoot && !runsAsRoot)
        {
            process.StartInfo.ArgumentList.Add("-n");
            process.StartInfo.ArgumentList.Add(managerPath);
        }
        process.StartInfo.ArgumentList.Add(action);

        try
        {
            if (!process.Start())
            {
                return ManagerCommandResult.Failure("Desktop Helper setup command did not start.");
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync(linkedCancellation.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(linkedCancellation.Token);
            await process.WaitForExitAsync(linkedCancellation.Token);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            if (process.ExitCode == 0)
            {
                return ManagerCommandResult.Success(standardOutput);
            }

            return ManagerCommandResult.Failure(BuildFailureMessage(process.ExitCode, standardError, standardOutput));
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested &&
                                                !cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            return ManagerCommandResult.Failure("Desktop Helper setup timed out and was stopped. LMS itself was not changed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryKillProcessTree(process);
            return ManagerCommandResult.Failure(exception.Message);
        }
    }

    private static string BuildFailureMessage(int exitCode, string standardError, string standardOutput)
    {
        var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        return string.IsNullOrWhiteSpace(detail)
            ? $"Desktop Helper setup exited with code {exitCode}."
            : detail.Trim();
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

    private static DesktopHelperSetupStatus Unmanaged(string detail) =>
        new(false, false, "Not managed", detail);

    private static GraphicalDesktopStatus UnmanagedGraphicalDesktop(string detail) =>
        new(false, false, false, "unknown", "unknown", "Not managed", detail);

    private sealed record ManagerCommandResult(bool Succeeded, string StandardOutput, string Message)
    {
        public static ManagerCommandResult Success(string output) => new(true, output, string.Empty);

        public static ManagerCommandResult Failure(string message) => new(false, string.Empty, message);
    }
}
