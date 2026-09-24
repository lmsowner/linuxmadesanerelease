// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Monitoring;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class LocalSystemMaintenanceService : ILocalSystemMaintenanceService
{
    private static readonly TimeSpan ProcessCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RebootCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupCommandTimeout = TimeSpan.FromMinutes(30);
    private readonly ILinuxCommandRunner commandRunner;
    private readonly Func<int, long?> processStartTimeReader;
    private readonly Func<long?> availableBytesReader;
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public LocalSystemMaintenanceService(ILinuxCommandRunner commandRunner)
        : this(commandRunner, ReadProcessStartTimeTicks, ReadRootAvailableBytes)
    {
    }

    internal LocalSystemMaintenanceService(
        ILinuxCommandRunner commandRunner,
        Func<int, long?> processStartTimeReader,
        Func<long?> availableBytesReader)
    {
        this.commandRunner = commandRunner;
        this.processStartTimeReader = processStartTimeReader;
        this.availableBytesReader = availableBytesReader;
    }

    public async Task<string> EndProcessAsync(
        int processId,
        long expectedStartTimeTicks,
        string expectedName,
        CancellationToken cancellationToken = default)
    {
        EnsureLinux();
        if (processId <= 1)
        {
            throw new InvalidOperationException("PID 1 and invalid process IDs cannot be ended from LMS.");
        }

        if (processId == Environment.ProcessId)
        {
            throw new InvalidOperationException("LMS cannot end its own server process from System Info.");
        }

        if (expectedStartTimeTicks <= 0)
        {
            throw new InvalidOperationException("The selected process does not have a valid kernel identity token.");
        }

        var processName = string.IsNullOrWhiteSpace(expectedName) ? "Process" : expectedName.Trim();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var actualStartTimeTicks = processStartTimeReader(processId);
            if (!actualStartTimeTicks.HasValue)
            {
                return $"{processName} (PID {processId}) had already ended.";
            }

            if (actualStartTimeTicks.Value != expectedStartTimeTicks)
            {
                throw new InvalidOperationException(
                    $"PID {processId} now belongs to a different process. LMS refused to send a signal; refresh the process list.");
            }

            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "kill",
                    ["-TERM", "--", processId.ToString(CultureInfo.InvariantCulture)],
                    RequiresSudo: true,
                    Timeout: ProcessCommandTimeout,
                    Description: $"End process {processId}"),
                dryRun: false,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                if (!processStartTimeReader(processId).HasValue)
                {
                    return $"{processName} (PID {processId}) ended before the termination signal was delivered.";
                }

                throw new InvalidOperationException(
                    $"LMS could not end {processName} (PID {processId}): {BuildFailureDetail(result)}");
            }

            return $"Termination signal sent to {processName} (PID {processId}).";
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LocalDiskCleanupResult> CleanupDiskAsync(CancellationToken cancellationToken = default)
    {
        EnsureLinux();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var availableBefore = availableBytesReader();
            var steps = new List<LocalDiskCleanupStepResult>
            {
                await RunCleanupStepAsync(
                    "System journal",
                    "journalctl",
                    ["--rotate", "--vacuum-time=14d"],
                    cancellationToken),
                await RunCleanupStepAsync(
                    "APT package cache",
                    "apt-get",
                    ["clean"],
                    cancellationToken),
                await RunCleanupStepAsync(
                    "Unused packages and old kernels",
                    "apt-get",
                    ["autoremove", "--purge", "--yes"],
                    cancellationToken)
            };
            var availableAfter = availableBytesReader();
            var reclaimedBytes = availableBefore.HasValue && availableAfter.HasValue
                ? Math.Max(0, availableAfter.Value - availableBefore.Value)
                : (long?)null;
            return new LocalDiskCleanupResult(reclaimedBytes, steps);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task RebootAsync(CancellationToken cancellationToken = default)
    {
        EnsureLinux();
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "systemctl",
                    ["reboot"],
                    RequiresSudo: true,
                    Timeout: RebootCommandTimeout,
                    Description: "Reboot the LMS server"),
                dryRun: false,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"The reboot request failed: {BuildFailureDetail(result)}");
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<LocalDiskCleanupStepResult> RunCleanupStepAsync(
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                executable,
                arguments,
                RequiresSudo: true,
                Timeout: CleanupCommandTimeout,
                Description: $"Clean disk space: {name}"),
            dryRun: false,
            cancellationToken);
        return new LocalDiskCleanupStepResult(
            name,
            result.ExitCode == 0,
            result.ExitCode == 0 ? BuildSuccessDetail(result) : BuildFailureDetail(result));
    }

    private static long? ReadProcessStartTimeTicks(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
            var closeName = stat.LastIndexOf(')');
            if (closeName < 0 || closeName + 2 >= stat.Length)
            {
                return null;
            }

            var fields = stat[(closeName + 2)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 19 &&
                   long.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTimeTicks)
                ? startTimeTicks
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadRootAvailableBytes()
    {
        try
        {
            return new DriveInfo("/").AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSuccessDetail(LinuxCommandResult result) =>
        LastOutputLine(result.StandardOutput) ??
        LastOutputLine(result.StandardError) ??
        "Completed.";

    private static string BuildFailureDetail(LinuxCommandResult result) =>
        LastOutputLine(result.StandardError) ??
        LastOutputLine(result.StandardOutput) ??
        $"Command exited with code {result.ExitCode}.";

    private static string? LastOutputLine(string output)
    {
        var line = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return line.Length <= 320 ? line : $"{line[..317]}...";
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Local system maintenance is available only on Linux hosts.");
        }
    }
}
