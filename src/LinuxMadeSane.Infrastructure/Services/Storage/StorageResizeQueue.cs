// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Collections.Concurrent;
using System.Threading.Channels;
using LinuxMadeSane.Application.Contracts.Storage;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services.Storage;

public sealed class StorageResizeQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<StorageResizeQueue> logger,
    TimeProvider timeProvider) : BackgroundService, IStorageResizeExecutor
{
    private const long MiB = 1024L * 1024L;
    private readonly Channel<Guid> queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, byte> queuedIds = new();
    private readonly ConcurrentDictionary<Guid, byte> cancelledIds = new();

    public async Task<StorageExecutionResult> QueueAsync(
        StorageResizePlan plan,
        StorageExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (plan.Id != request.PlanId)
        {
            throw new InvalidOperationException("The execution request does not match the resize plan.");
        }

        if (plan.OperationType == StorageResizeDirection.Shrink && !request.BackupAcknowledged)
        {
            throw new InvalidOperationException("Confirm that a current backup exists before shrinking storage.");
        }

        if ((plan.OperationType is StorageResizeDirection.AttachMount or StorageResizeDirection.AttachToVolumeGroup) &&
            !request.BackupAcknowledged)
        {
            throw new InvalidOperationException("Confirm that you understand this unused disk will be erased before continuing.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStorageOperationRepository>();
        if (await repository.HasActiveOperationForDiskAsync(plan.TargetDiskDevicePath, cancellationToken))
        {
            throw new InvalidOperationException($"Another storage operation is already active on {plan.TargetDiskDevicePath}.");
        }

        var now = timeProvider.GetUtcNow();
        var existing = await repository.GetAsync(plan.Id, cancellationToken);
        if (existing is not null && existing.State is StorageOperationState.Running or StorageOperationState.Verifying or StorageOperationState.Completed)
        {
            return new StorageExecutionResult(existing.Id, existing.State, "This storage plan has already been started.");
        }

        var operation = new StorageResizeOperation(
            plan.Id,
            plan,
            StorageOperationState.Queued,
            CleanActor(request.RequestedBy),
            Environment.MachineName,
            request.BackupAcknowledged,
            existing?.CreatedUtc ?? now,
            now,
            null,
            null,
            null,
            null,
            null);
        await repository.SaveAsync(operation, cancellationToken);
        Enqueue(operation.Id);
        return new StorageExecutionResult(operation.Id, operation.State, "The validated resize is queued. It will continue if the browser disconnects.");
    }

    public async Task<bool> CancelAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStorageOperationRepository>();
        var operation = await repository.GetAsync(operationId, cancellationToken);
        if (operation is null || operation.State is not (StorageOperationState.Created or StorageOperationState.Validated or StorageOperationState.AwaitingConfirmation or StorageOperationState.Queued))
        {
            return false;
        }

        cancelledIds[operationId] = 0;
        var now = timeProvider.GetUtcNow();
        await repository.SaveAsync(operation with
        {
            State = StorageOperationState.Cancelled,
            UpdatedUtc = now,
            CompletedUtc = now
        }, cancellationToken);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedOperationsAsync(stoppingToken);
        await foreach (var operationId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            queuedIds.TryRemove(operationId, out _);
            if (cancelledIds.TryRemove(operationId, out _))
            {
                continue;
            }

            try
            {
                await ExecuteOperationAsync(operationId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Storage resize operation {OperationId} stopped unexpectedly", operationId);
                await RecordUnexpectedFailureAsync(operationId, exception.Message);
            }
        }
    }

    private async Task RecoverInterruptedOperationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStorageOperationRepository>();
        var recent = await repository.ListAsync(500, cancellationToken);
        foreach (var operation in recent)
        {
            if (operation.State is StorageOperationState.Running or StorageOperationState.Verifying)
            {
                var now = timeProvider.GetUtcNow();
                await repository.SaveAsync(operation with
                {
                    State = StorageOperationState.RecoveryRequired,
                    UpdatedUtc = now,
                    CompletedUtc = now,
                    FailureDetail = "LMS restarted while this storage operation was running. Review completed steps before taking further action."
                }, cancellationToken);
            }
            else if (operation.State == StorageOperationState.Queued)
            {
                Enqueue(operation.Id);
            }
        }
    }

    private async Task ExecuteOperationAsync(Guid operationId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStorageOperationRepository>();
        var planner = scope.ServiceProvider.GetRequiredService<IStorageResizePlanner>();
        var attachmentPlanner = scope.ServiceProvider.GetRequiredService<IStorageDiskAttachmentPlanner>();
        var discovery = scope.ServiceProvider.GetRequiredService<IStorageDiscoveryService>();
        var commandRunner = scope.ServiceProvider.GetRequiredService<ILinuxCommandRunner>();
        var operation = await repository.GetAsync(operationId) ?? throw new InvalidOperationException("Queued storage operation was not found.");
        if (operation.State != StorageOperationState.Queued)
        {
            return;
        }

        var before = await discovery.DiscoverAsync();
        var validation = operation.Plan.OperationType is StorageResizeDirection.AttachMount or StorageResizeDirection.AttachToVolumeGroup
            ? await attachmentPlanner.ValidatePlanAsync(operation.Plan)
            : await planner.ValidatePlanAsync(operation.Plan);
        operation = operation with
        {
            Plan = operation.Plan with { ValidationResults = validation },
            BeforeTopology = before,
            State = StorageOperationState.Validated,
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await repository.SaveAsync(operation);

        var failures = validation.Where(result => !result.Passed && result.Severity == StorageValidationSeverity.Error).ToArray();
        if (failures.Length > 0)
        {
            await FailAsync(repository, operation, StorageOperationState.Failed,
                string.Join(" ", failures.Select(result => $"{result.Check}: {result.Detail}")));
            return;
        }

        operation = operation with
        {
            State = StorageOperationState.Running,
            StartedUtc = timeProvider.GetUtcNow(),
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await repository.SaveAsync(operation);

        var completedMutation = false;
        var logicalVolumeShrunk = false;
        var isUnmounted = false;
        foreach (var originalStep in operation.Plan.Steps.OrderBy(step => step.Order))
        {
            var runningStep = originalStep with { State = StorageStepState.Running, StartedUtc = timeProvider.GetUtcNow() };
            operation = WithStep(operation, runningStep);
            operation = operation with
            {
                State = originalStep.Operation == StorageResizeStepOperation.Verify
                    ? StorageOperationState.Verifying
                    : StorageOperationState.Running,
                UpdatedUtc = timeProvider.GetUtcNow()
            };
            await repository.SaveAsync(operation);

            if (originalStep.Operation == StorageResizeStepOperation.Verify)
            {
                var finalTopology = await discovery.DiscoverAsync();
                var verification = VerifyFinal(operation.Plan, finalTopology);
                var verifiedStep = runningStep with
                {
                    State = verification.Passed ? StorageStepState.Completed : StorageStepState.Failed,
                    CompletedUtc = timeProvider.GetUtcNow(),
                    ExitCode = verification.Passed ? 0 : 1,
                    VerificationDetail = verification.Detail
                };
                operation = WithStep(operation, verifiedStep) with { AfterTopology = finalTopology };
                await repository.SaveAsync(operation);
                if (!verification.Passed)
                {
                    await FailAsync(
                        repository,
                        operation,
                        logicalVolumeShrunk ? StorageOperationState.RecoveryRequired : StorageOperationState.Failed,
                        verification.Detail);
                    return;
                }

                continue;
            }

            if (originalStep.Operation == StorageResizeStepOperation.UpdateFstab)
            {
                var fstabResult = await ApplyFstabAsync(commandRunner, originalStep.Arguments);
                var fstabStep = runningStep with
                {
                    State = fstabResult.Passed ? StorageStepState.Completed : StorageStepState.Failed,
                    CompletedUtc = timeProvider.GetUtcNow(),
                    ExitCode = fstabResult.Passed ? 0 : 1,
                    StandardOutput = fstabResult.Detail,
                    VerificationDetail = fstabResult.Detail
                };
                operation = WithStep(operation, fstabStep) with { UpdatedUtc = timeProvider.GetUtcNow() };
                await repository.SaveAsync(operation);
                if (!fstabResult.Passed)
                {
                    await FailAsync(repository, operation, StorageOperationState.Failed, fstabResult.Detail);
                    return;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(originalStep.Executable))
            {
                await FailAsync(repository, operation, StorageOperationState.Failed, $"Step '{originalStep.Title}' has no executable action.");
                return;
            }

            var result = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    originalStep.Executable,
                    originalStep.Arguments,
                    RequiresSudo: true,
                    TimeoutFor(originalStep.Operation),
                    originalStep.Title),
                dryRun: false,
                CancellationToken.None);
            var succeeded = result.ExitCode == 0 ||
                            originalStep.Operation == StorageResizeStepOperation.FileSystemCheck && result.ExitCode == 1;
            var completedStep = runningStep with
            {
                State = succeeded ? StorageStepState.Completed : StorageStepState.Failed,
                CompletedUtc = timeProvider.GetUtcNow(),
                ExitCode = result.ExitCode,
                StandardOutput = Limit(result.StandardOutput),
                StandardError = Limit(result.StandardError)
            };
            operation = WithStep(operation, completedStep) with { UpdatedUtc = timeProvider.GetUtcNow() };
            await repository.SaveAsync(operation);

            if (!succeeded)
            {
                if (isUnmounted && !logicalVolumeShrunk)
                {
                    var remounted = await TryRestoreMountAsync(commandRunner, operation.Plan);
                    isUnmounted = !remounted;
                }

                var failureState = logicalVolumeShrunk && isUnmounted
                    ? StorageOperationState.RecoveryRequired
                    : StorageOperationState.Failed;
                await FailAsync(repository, operation, failureState,
                    $"{originalStep.Title} failed (exit {result.ExitCode}). {FirstNonEmpty(result.StandardError, result.StandardOutput)}");
                return;
            }

            isUnmounted = originalStep.Operation switch
            {
                StorageResizeStepOperation.Unmount => true,
                StorageResizeStepOperation.Mount => false,
                _ => isUnmounted
            };
            logicalVolumeShrunk |= originalStep.Operation == StorageResizeStepOperation.LvShrink;
            completedMutation |= originalStep.IsMutation;

            if (originalStep.IsMutation)
            {
                var mutationVerification = await VerifyMutationAsync(operation, originalStep.Operation, discovery, commandRunner);
                var amendedStep = completedStep with { VerificationDetail = mutationVerification.Detail };
                operation = WithStep(operation, amendedStep) with
                {
                    AfterTopology = mutationVerification.Topology,
                    UpdatedUtc = timeProvider.GetUtcNow()
                };
                await repository.SaveAsync(operation);
                if (!mutationVerification.Passed)
                {
                    await FailAsync(
                        repository,
                        operation,
                        logicalVolumeShrunk ? StorageOperationState.RecoveryRequired : StorageOperationState.Failed,
                        mutationVerification.Detail);
                    return;
                }
            }
        }

        var final = operation.AfterTopology ?? await discovery.DiscoverAsync();
        var now = timeProvider.GetUtcNow();
        operation = operation with
        {
            State = StorageOperationState.Completed,
            UpdatedUtc = now,
            CompletedUtc = now,
            AfterTopology = final,
            FailureDetail = null
        };
        await repository.SaveAsync(operation);
        logger.LogInformation(
            "Storage resize {OperationId} completed for {MountPoint}: {BeforeBytes} -> {AfterBytes}",
            operation.Id,
            operation.Plan.TargetMountPoint,
            operation.Plan.CurrentSizeBytes,
            operation.Plan.RequestedSizeBytes);
    }

    private static async Task<MutationVerification> VerifyMutationAsync(
        StorageResizeOperation operation,
        StorageResizeStepOperation step,
        IStorageDiscoveryService discovery,
        ILinuxCommandRunner commandRunner)
    {
        StorageTopologySnapshot? topology;
        try
        {
            topology = await discovery.DiscoverAsync();
        }
        catch (Exception exception)
        {
            return new MutationVerification(false, $"Storage rediscovery failed after {step}: {exception.Message}", null);
        }

        var current = topology.FileSystems.SingleOrDefault(item => item.Id == operation.Plan.TargetFileSystemId);
        switch (step)
        {
            case StorageResizeStepOperation.PartitionGrow:
            {
                var beforeFileSystem = operation.BeforeTopology?.FileSystems.SingleOrDefault(item => item.Id == operation.Plan.TargetFileSystemId);
                var partitionPath = beforeFileSystem?.PartitionDevicePath;
                var beforeSize = FindNodeSize(operation.BeforeTopology, partitionPath);
                var afterSize = FindNodeSize(topology, partitionPath);
                var passed = beforeSize > 0 && afterSize > beforeSize;
                return new MutationVerification(passed,
                    passed ? $"Partition grew from {FormatBytes(beforeSize)} to {FormatBytes(afterSize)}." : "The partition size did not increase as planned.", topology);
            }
            case StorageResizeStepOperation.PvGrow:
                return new MutationVerification(current is not null,
                    current is not null ? "LVM rediscovery succeeded after resizing the physical volume." : "The LVM filesystem could not be rediscovered after pvresize.", topology);
            case StorageResizeStepOperation.LvGrow:
            {
                var passed = current is not null && current.ContainerSizeBytes >= operation.Plan.RequestedSizeBytes - 8 * MiB;
                return new MutationVerification(passed,
                    passed ? $"Logical volume is now {FormatBytes(current!.ContainerSizeBytes)}." : "The logical volume did not reach the requested size.", topology);
            }
            case StorageResizeStepOperation.LvShrink:
            {
                var size = await ReadBlockDeviceSizeAsync(commandRunner, operation.Plan.TargetDevicePath);
                var tolerance = Math.Max(8 * MiB, operation.Plan.RequestedSizeBytes / 100);
                var passed = size > 0 && Math.Abs(size - operation.Plan.RequestedSizeBytes) <= tolerance;
                return new MutationVerification(passed,
                    passed ? $"Logical volume is now {FormatBytes(size)}." : "The logical volume size could not be verified after shrinking.", topology);
            }
            case StorageResizeStepOperation.FileSystemShrink:
            {
                var size = await ReadExt4SizeAsync(commandRunner, operation.Plan.TargetDevicePath);
                var passed = size > 0 && size < operation.Plan.CurrentSizeBytes && size <= operation.Plan.RequestedSizeBytes;
                return new MutationVerification(passed,
                    passed ? $"EXT4 safely shrank inside its unchanged container ({FormatBytes(size)})." : "The EXT4 shrink result could not be verified. The logical volume was not reduced.", topology);
            }
            case StorageResizeStepOperation.FileSystemGrow:
            {
                var expected = operation.Plan.OperationType == StorageResizeDirection.Shrink
                    ? operation.Plan.RequestedSizeBytes
                    : operation.Plan.RequestedSizeBytes;
                var size = current?.CurrentSizeBytes ?? await ReadExt4SizeAsync(commandRunner, operation.Plan.TargetDevicePath);
                var tolerance = Math.Max(128 * MiB, expected / 50);
                var passed = size >= expected - tolerance;
                return new MutationVerification(passed,
                    passed ? $"Filesystem size verified at {FormatBytes(size)}." : $"Filesystem is {FormatBytes(size)}; expected approximately {FormatBytes(expected)}.", topology);
            }
            default:
                return new MutationVerification(true, "Storage topology rediscovery succeeded.", topology);
        }
    }

    private static (bool Passed, string Detail) VerifyFinal(StorageResizePlan plan, StorageTopologySnapshot topology)
    {
        if (plan.OperationType == StorageResizeDirection.AttachMount)
        {
            var mounted = topology.FileSystems.FirstOrDefault(item =>
                item.MountPoint.Equals(plan.TargetMountPoint, StringComparison.Ordinal));
            var passed = mounted is not null &&
                         (mounted.DiskDevicePath == plan.TargetDiskDevicePath ||
                          mounted.DevicePath.StartsWith(plan.TargetDiskDevicePath, StringComparison.Ordinal));
            return passed
                ? (true, $"{plan.TargetMountPoint} is mounted from the new disk.")
                : (false, $"{plan.TargetMountPoint} is not mounted from {plan.TargetDiskDevicePath}.");
        }

        if (plan.OperationType == StorageResizeDirection.AttachToVolumeGroup)
        {
            var volumeGroup = topology.VolumeGroups.FirstOrDefault(group =>
                group.Name.Equals(plan.TargetMountPoint, StringComparison.Ordinal));
            var passed = volumeGroup is not null &&
                         volumeGroup.PhysicalVolumePaths.Any(path =>
                             path.StartsWith(plan.TargetDiskDevicePath, StringComparison.Ordinal));
            return passed
                ? (true, $"{plan.TargetMountPoint} now includes capacity from {plan.TargetDiskDevicePath}.")
                : (false, $"{plan.TargetDiskDevicePath} was not found in volume group {plan.TargetMountPoint}.");
        }

        var fileSystem = topology.FileSystems.SingleOrDefault(item => item.Id == plan.TargetFileSystemId) ??
                         topology.FileSystems.SingleOrDefault(item =>
                             item.MountPoint.Equals(plan.TargetMountPoint, StringComparison.Ordinal) &&
                             item.FileSystemType.Equals(plan.TargetFileSystemType, StringComparison.OrdinalIgnoreCase));
        if (fileSystem is null)
        {
            return (false, $"{plan.TargetMountPoint} is not mounted after the resize.");
        }

        var tolerance = Math.Max(128 * MiB, plan.RequestedSizeBytes / 50);
        var passedSize = plan.OperationType == StorageResizeDirection.Grow
            ? fileSystem.CurrentSizeBytes >= plan.RequestedSizeBytes - tolerance
            : Math.Abs(fileSystem.CurrentSizeBytes - plan.RequestedSizeBytes) <= tolerance;
        return passedSize
            ? (true, $"{plan.TargetMountPoint} is mounted and reports {FormatBytes(fileSystem.CurrentSizeBytes)}.")
            : (false, $"{plan.TargetMountPoint} reports {FormatBytes(fileSystem.CurrentSizeBytes)}; expected approximately {FormatBytes(plan.RequestedSizeBytes)}.");
    }

    private static async Task<(bool Passed, string Detail)> ApplyFstabAsync(
        ILinuxCommandRunner runner,
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 6)
        {
            return (false, "The fstab step was missing required arguments.");
        }

        var devicePath = arguments[0];
        var mountPoint = arguments[1];
        var fsType = arguments[2];
        var options = arguments[3];
        var dump = arguments[4];
        var pass = arguments[5];
        var uuidResult = await runner.RunAsync(
            new LinuxCommandRequest("blkid", ["-s", "UUID", "-o", "value", devicePath], true, TimeSpan.FromSeconds(15), "Read filesystem UUID"),
            dryRun: false,
            CancellationToken.None);
        if (uuidResult.ExitCode != 0 || string.IsNullOrWhiteSpace(uuidResult.StandardOutput))
        {
            return (false, $"Could not read a UUID for {devicePath}.");
        }

        var uuid = uuidResult.StandardOutput.Trim();
        var marker = "# linuxmadesane-storage";
        var entry = $"UUID={uuid} {EscapeFstab(mountPoint)} {fsType} {options} {dump} {pass} {marker}";
        var existing = File.Exists("/etc/fstab")
            ? (await File.ReadAllLinesAsync("/etc/fstab")).ToList()
            : [];
        existing.RemoveAll(line =>
            line.Contains(marker, StringComparison.Ordinal) &&
            line.Contains($" {mountPoint} ", StringComparison.Ordinal));
        existing.Add(entry);

        var stagingDirectory = "/var/lib/linuxmadesane/storage-staging";
        Directory.CreateDirectory(stagingDirectory);
        var staged = Path.Combine(stagingDirectory, "fstab.storage.lms");
        await File.WriteAllTextAsync(staged, string.Join('\n', existing) + "\n");
        var copy = await runner.RunAsync(
            new LinuxCommandRequest("cp", [staged, "/etc/fstab"], true, TimeSpan.FromSeconds(15), "Update /etc/fstab"),
            dryRun: false,
            CancellationToken.None);
        return copy.ExitCode == 0
            ? (true, $"Added UUID={uuid} for {mountPoint} to /etc/fstab.")
            : (false, FirstNonEmpty(copy.StandardError, copy.StandardOutput));
    }

    private static string EscapeFstab(string value) =>
        value.Contains(' ', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal)
            ? $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;

    private static long FindNodeSize(StorageTopologySnapshot? topology, string? devicePath) =>
        topology?.Disks.SelectMany(disk => disk.Nodes)
            .FirstOrDefault(node => node.DevicePath.Equals(devicePath, StringComparison.Ordinal))?.SizeBytes ?? 0;

    private static async Task<long> ReadBlockDeviceSizeAsync(ILinuxCommandRunner runner, string devicePath)
    {
        var result = await runner.RunAsync(
            new LinuxCommandRequest("blockdev", ["--getsize64", devicePath], true, TimeSpan.FromSeconds(15), "Verify block device size"),
            dryRun: false,
            CancellationToken.None);
        return result.ExitCode == 0 && long.TryParse(result.StandardOutput.Trim(), out var size) ? size : 0;
    }

    private static async Task<long> ReadExt4SizeAsync(ILinuxCommandRunner runner, string devicePath)
    {
        var result = await runner.RunAsync(
            new LinuxCommandRequest("tune2fs", ["-l", devicePath], true, TimeSpan.FromSeconds(30), "Verify EXT4 size"),
            dryRun: false,
            CancellationToken.None);
        if (result.ExitCode != 0) return 0;
        long blocks = 0;
        long blockSize = 0;
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("Block count", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out blocks);
            if (parts[0].Equals("Block size", StringComparison.OrdinalIgnoreCase)) long.TryParse(parts[1], out blockSize);
        }

        return blocks > 0 && blockSize > 0 ? checked(blocks * blockSize) : 0;
    }

    private static async Task<bool> TryRestoreMountAsync(ILinuxCommandRunner runner, StorageResizePlan plan)
    {
        var mountStep = plan.Steps.FirstOrDefault(step => step.Operation == StorageResizeStepOperation.Mount);
        if (mountStep?.Executable is null)
        {
            return false;
        }

        var result = await runner.RunAsync(
            new LinuxCommandRequest(mountStep.Executable, mountStep.Arguments, true, TimeSpan.FromSeconds(30), $"Restore {plan.TargetMountPoint}"),
            dryRun: false,
            CancellationToken.None);
        return result.ExitCode == 0;
    }

    private static StorageResizeOperation WithStep(StorageResizeOperation operation, StorageResizeStep step)
    {
        var steps = operation.Plan.Steps.Select(item => item.Order == step.Order ? step : item).ToArray();
        return operation with { Plan = operation.Plan with { Steps = steps } };
    }

    private async Task FailAsync(
        IStorageOperationRepository repository,
        StorageResizeOperation operation,
        StorageOperationState state,
        string detail)
    {
        var now = timeProvider.GetUtcNow();
        await repository.SaveAsync(operation with
        {
            State = state,
            UpdatedUtc = now,
            CompletedUtc = now,
            FailureDetail = Limit(detail, 4000)
        });
    }

    private async Task RecordUnexpectedFailureAsync(Guid operationId, string detail)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IStorageOperationRepository>();
            var operation = await repository.GetAsync(operationId);
            if (operation is not null && operation.State is not (StorageOperationState.Completed or StorageOperationState.Cancelled))
            {
                await FailAsync(repository, operation, StorageOperationState.RecoveryRequired, detail);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not persist unexpected failure for storage operation {OperationId}", operationId);
        }
    }

    private void Enqueue(Guid id)
    {
        if (queuedIds.TryAdd(id, 0))
        {
            queue.Writer.TryWrite(id);
        }
    }

    private static TimeSpan TimeoutFor(StorageResizeStepOperation operation) => operation switch
    {
        StorageResizeStepOperation.FileSystemCheck => TimeSpan.FromHours(4),
        StorageResizeStepOperation.FileSystemShrink => TimeSpan.FromHours(4),
        StorageResizeStepOperation.FileSystemGrow => TimeSpan.FromHours(2),
        _ => TimeSpan.FromMinutes(10)
    };

    private static string CleanActor(string value)
    {
        var clean = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(clean) ? "Local LMS administrator" : clean[..Math.Min(clean.Length, 320)];
    }

    private static string FirstNonEmpty(string first, string second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : !string.IsNullOrWhiteSpace(second) ? second.Trim() : "No diagnostic output was returned.";

    private static string Limit(string? value, int maxLength = 12000)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static string FormatBytes(long bytes)
    {
        var gib = bytes / (double)(1024L * 1024L * 1024L);
        return gib >= 1024 ? $"{gib / 1024:0.##} TB" : $"{gib:0.##} GB";
    }

    private sealed record MutationVerification(bool Passed, string Detail, StorageTopologySnapshot? Topology);
}
