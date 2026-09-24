// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.Storage;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services.Storage;

public sealed partial class LinuxStorageResizePlanner(
    IStorageDiscoveryService discoveryService,
    ILinuxCommandRunner commandRunner,
    TimeProvider timeProvider) : IStorageResizePlanner
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(30);

    public async Task<StorageResizePlan> CreatePlanAsync(
        StorageResizePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        var topology = await discoveryService.DiscoverAsync(cancellationToken);
        var fileSystem = topology.FileSystems.SingleOrDefault(item => item.Id == request.FileSystemId)
            ?? throw new InvalidOperationException("The selected filesystem no longer exists. Refresh Storage and choose it again.");
        var target = request.UseMaximum ? fileSystem.MaximumSizeBytes : request.RequestedSizeBytes;
        var alignment = fileSystem.LvmExtentSizeBytes > 0 ? fileSystem.LvmExtentSizeBytes : 4 * MiB;
        target = AlignDown(target, alignment);

        if (target == fileSystem.CurrentSizeBytes || Math.Abs(target - fileSystem.CurrentSizeBytes) < MiB)
        {
            throw new InvalidOperationException("Choose a size that is meaningfully different from the current filesystem size.");
        }

        return target > fileSystem.CurrentSizeBytes
            ? await BuildGrowPlanAsync(fileSystem, target, request.UseMaximum, topology, cancellationToken)
            : await BuildShrinkPlanAsync(fileSystem, target, topology, cancellationToken);
    }

    public async Task<IReadOnlyList<StorageValidationResult>> ValidatePlanAsync(
        StorageResizePlan plan,
        CancellationToken cancellationToken = default)
    {
        var results = new List<StorageValidationResult>();
        var current = await discoveryService.DiscoverAsync(cancellationToken);
        var fileSystem = current.FileSystems.SingleOrDefault(item => item.Id == plan.TargetFileSystemId);
        results.Add(new StorageValidationResult(
            "Filesystem visible",
            fileSystem is not null,
            fileSystem is null ? "The target filesystem is no longer present." : $"{fileSystem.DevicePath} is mounted at {fileSystem.MountPoint}."));
        results.Add(new StorageValidationResult(
            "Storage topology unchanged",
            current.Fingerprint.Equals(plan.TopologyFingerprint, StringComparison.Ordinal),
            current.Fingerprint.Equals(plan.TopologyFingerprint, StringComparison.Ordinal)
                ? "The disk, partition, LVM and filesystem sizes still match this plan."
                : "Storage changed after this plan was created. Create a fresh plan before resizing."));

        if (fileSystem is not null)
        {
            results.Add(new StorageValidationResult(
                "Filesystem type",
                fileSystem.FileSystemType.Equals(plan.TargetFileSystemType, StringComparison.OrdinalIgnoreCase),
                $"Detected {fileSystem.FileSystemType}."));
            results.Add(new StorageValidationResult(
                "Target size",
                plan.RequestedSizeBytes >= plan.MinimumSizeBytes && plan.RequestedSizeBytes <= plan.MaximumSizeBytes,
                $"Allowed range is {FormatBytes(plan.MinimumSizeBytes)} to {FormatBytes(plan.MaximumSizeBytes)}."));
            results.Add(new StorageValidationResult(
                "Block device writable",
                !fileSystem.IsReadOnly,
                fileSystem.IsReadOnly ? "The target is read-only." : "The target block device is writable."));
            var disk = current.Disks.FirstOrDefault(item => item.DevicePath == fileSystem.DiskDevicePath);
            if (disk?.Health == "Failing")
            {
                results.Add(new StorageValidationResult("Disk health", false, "SMART/NVMe reports that the disk is failing."));
            }
            else if (disk?.Health is "Unknown" or "Not checked" or null)
            {
                results.Add(new StorageValidationResult(
                    "Disk health",
                    false,
                    "SMART/NVMe health is unavailable. This does not by itself prevent a validated resize.",
                    StorageValidationSeverity.Warning));
            }
            else
            {
                results.Add(new StorageValidationResult("Disk health", true, "SMART/NVMe does not report a failure."));
            }

            results.Add(await CheckMdRaidIdleAsync(cancellationToken));
            results.Add(await CheckLvmConflictsAsync(fileSystem, cancellationToken));
        }

        foreach (var executable in plan.Steps
                     .Select(step => step.Executable)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Cast<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            var probe = await commandRunner.RunAsync(
                new LinuxCommandRequest(
                    "which",
                    [executable],
                    RequiresSudo: false,
                    TimeSpan.FromSeconds(5),
                    $"Check storage tool {executable}")
                {
                    IsOptionalExternalTool = true
                },
                dryRun: false,
                cancellationToken);
            results.Add(new StorageValidationResult(
                $"Tool: {executable}",
                probe.ExitCode == 0,
                probe.ExitCode == 0 ? $"{executable} is installed." : $"{executable} is required for this resize plan."));
        }

        if (plan.OperationType == StorageResizeDirection.Shrink && fileSystem is not null)
        {
            var ext4 = await InspectExt4Async(fileSystem.DevicePath, fileSystem.UsedBytes, cancellationToken);
            results.Add(new StorageValidationResult(
                "EXT4 metadata",
                ext4.Success,
                ext4.Detail));
            results.Add(new StorageValidationResult(
                "Minimum safe size",
                ext4.Success && plan.RequestedSizeBytes >= ext4.LmsMinimumBytes,
                ext4.Success
                    ? $"Current LMS minimum is {FormatBytes(ext4.LmsMinimumBytes)}."
                    : "The EXT4 minimum size could not be confirmed."));
        }

        return results;
    }

    private async Task<StorageResizePlan> BuildGrowPlanAsync(
        StorageFileSystem fileSystem,
        long target,
        bool useMaximum,
        StorageTopologySnapshot topology,
        CancellationToken cancellationToken)
    {
        if (!fileSystem.CanGrow)
        {
            throw new InvalidOperationException(fileSystem.ResizeUnavailableReason ?? "This filesystem cannot currently grow.");
        }

        if (target > fileSystem.MaximumSizeBytes)
        {
            throw new InvalidOperationException($"The largest safe size is {FormatBytes(fileSystem.MaximumSizeBytes)}.");
        }

        if (fileSystem.FileSystemType == "xfs" &&
            fileSystem.LvmLogicalVolumePath is null &&
            (!useMaximum || target < fileSystem.MaximumSizeBytes - 4 * MiB))
        {
            throw new InvalidOperationException("A plain-partition XFS filesystem can only be expanded to the available maximum. XFS grows to its containing partition.");
        }

        var steps = new List<StorageResizeStep>();
        var needsContainerGrowth = target > fileSystem.ContainerSizeBytes + MiB;
        var lvmAvailableWithoutDiskGrowth = fileSystem.ContainerSizeBytes + fileSystem.VolumeGroupFreeBytes;
        var needsDiskGrowth = fileSystem.LvmLogicalVolumePath is null
            ? needsContainerGrowth
            : target > lvmAvailableWithoutDiskGrowth + MiB;

        if (needsDiskGrowth)
        {
            EnsurePartitionIdentity(fileSystem);
            AddCommand(
                steps,
                "Expand the partition",
                $"Use the unallocated space after {fileSystem.PartitionDevicePath}.",
                StorageResizeStepOperation.PartitionGrow,
                "growpart",
                [fileSystem.DiskDevicePath!, fileSystem.PartitionNumber!.Value.ToString(CultureInfo.InvariantCulture)],
                mutation: true);

            if (fileSystem.LvmLogicalVolumePath is not null)
            {
                if (string.IsNullOrWhiteSpace(fileSystem.LvmPhysicalVolumePath))
                {
                    throw new InvalidOperationException("The LVM physical volume could not be mapped safely.");
                }

                AddCommand(
                    steps,
                    "Make the new space available to LVM",
                    $"Resize the LVM physical volume on {fileSystem.LvmPhysicalVolumePath}.",
                    StorageResizeStepOperation.PvGrow,
                    "pvresize",
                    [fileSystem.LvmPhysicalVolumePath],
                    mutation: true);
            }
        }

        if (fileSystem.LvmLogicalVolumePath is not null && target > fileSystem.ContainerSizeBytes + MiB)
        {
            AddCommand(
                steps,
                "Extend the filesystem's storage",
                $"Grow {fileSystem.LvmLogicalVolumePath} to {FormatBytes(target)}.",
                StorageResizeStepOperation.LvGrow,
                "lvextend",
                ["--size", $"{target}B", fileSystem.LvmLogicalVolumePath],
                mutation: true);
        }

        if (fileSystem.FileSystemType == "ext4")
        {
            AddCommand(
                steps,
                "Grow the filesystem",
                $"Grow EXT4 to {FormatBytes(target)} while it remains online.",
                StorageResizeStepOperation.FileSystemGrow,
                "resize2fs",
                [fileSystem.DevicePath, $"{AlignDown(target, 1024) / 1024}K"],
                mutation: true);
        }
        else
        {
            AddCommand(
                steps,
                "Grow the filesystem",
                "Grow XFS to use its newly enlarged container.",
                StorageResizeStepOperation.FileSystemGrow,
                "xfs_growfs",
                [fileSystem.MountPoint],
                mutation: true);
        }

        AddVerification(steps, fileSystem, target);
        var warnings = new List<string>
        {
            "A current backup is recommended before modifying storage."
        };
        var validation = BuildInitialValidations(fileSystem, target, topology);
        var plan = new StorageResizePlan(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            fileSystem.Id,
            fileSystem.MountPoint,
            fileSystem.DevicePath,
            fileSystem.DiskDevicePath ?? string.Empty,
            fileSystem.FileSystemType,
            StorageResizeDirection.Grow,
            fileSystem.CurrentSizeBytes,
            target,
            fileSystem.CurrentSizeBytes,
            fileSystem.MaximumSizeBytes,
            false,
            false,
            false,
            StorageResizeRisk.Low,
            false,
            topology.Fingerprint,
            string.Empty,
            steps,
            warnings,
            validation);

        var fullValidation = await ValidatePlanAsync(plan, cancellationToken);
        return plan with { ValidationResults = fullValidation };
    }

    private async Task<StorageResizePlan> BuildShrinkPlanAsync(
        StorageFileSystem fileSystem,
        long target,
        StorageTopologySnapshot topology,
        CancellationToken cancellationToken)
    {
        if (fileSystem.FileSystemType == "xfs")
        {
            throw new InvalidOperationException("XFS filesystems cannot currently be shrunk safely. Create a smaller filesystem and migrate the data instead.");
        }

        if (fileSystem.IsRoot)
        {
            throw new InvalidOperationException("The root filesystem cannot safely be shrunk while Linux is running. Offline root shrinking is not enabled in this release.");
        }

        if (!fileSystem.CanShrink || fileSystem.LvmLogicalVolumePath is null)
        {
            throw new InvalidOperationException(fileSystem.ResizeUnavailableReason ?? "This storage layout cannot be shrunk safely.");
        }

        var ext4 = await InspectExt4Async(fileSystem.DevicePath, fileSystem.UsedBytes, cancellationToken);
        if (!ext4.Success)
        {
            throw new InvalidOperationException(ext4.Detail);
        }

        var minimum = Math.Max(fileSystem.MinimumSafeSizeBytes, ext4.LmsMinimumBytes);
        if (target < minimum)
        {
            throw new InvalidOperationException($"Cannot shrink to {FormatBytes(target)}. The current LMS safe minimum is {FormatBytes(minimum)}.");
        }

        var containerTarget = AlignDown(target, fileSystem.LvmExtentSizeBytes > 0 ? fileSystem.LvmExtentSizeBytes : 4 * MiB);
        var fileSystemTarget = AlignDown(containerTarget - 64 * MiB, ext4.BlockSizeBytes);
        if (fileSystemTarget < ext4.LmsMinimumBytes)
        {
            throw new InvalidOperationException("The requested size does not leave the required safety gap between EXT4 and the logical volume.");
        }

        var steps = new List<StorageResizeStep>();
        AddCommand(
            steps,
            "Unmount the filesystem",
            $"Temporarily unmount {fileSystem.MountPoint}.",
            StorageResizeStepOperation.Unmount,
            "umount",
            ["--", fileSystem.MountPoint],
            mutation: false);
        AddCommand(
            steps,
            "Check the filesystem",
            "Run a complete EXT4 consistency check before changing its size.",
            StorageResizeStepOperation.FileSystemCheck,
            "e2fsck",
            ["-f", "-p", fileSystem.DevicePath],
            mutation: false);
        AddCommand(
            steps,
            "Shrink the filesystem",
            $"Shrink EXT4 inside the logical volume, retaining a 64 MiB safety gap.",
            StorageResizeStepOperation.FileSystemShrink,
            "resize2fs",
            [fileSystem.DevicePath, $"{fileSystemTarget / 1024}K"],
            mutation: true);
        AddCommand(
            steps,
            "Shrink the logical volume",
            $"Reduce {fileSystem.LvmLogicalVolumePath} to {FormatBytes(containerTarget)}.",
            StorageResizeStepOperation.LvShrink,
            "lvreduce",
            ["--yes", "--size", $"{containerTarget}B", fileSystem.LvmLogicalVolumePath],
            mutation: true);
        AddCommand(
            steps,
            "Fill the resized logical volume",
            "Grow EXT4 to use the small safety gap left during the inside-out shrink.",
            StorageResizeStepOperation.FileSystemGrow,
            "resize2fs",
            [fileSystem.DevicePath],
            mutation: true);
        AddCommand(
            steps,
            "Mount the filesystem",
            $"Mount {fileSystem.MountPoint} again with its existing mount options.",
            StorageResizeStepOperation.Mount,
            "mount",
            BuildMountArguments(fileSystem),
            mutation: false);
        AddVerification(steps, fileSystem, containerTarget);

        var freed = Math.Max(0, fileSystem.CurrentSizeBytes - containerTarget);
        var destination = string.IsNullOrWhiteSpace(fileSystem.LvmVolumeGroup)
            ? "unallocated disk space"
            : $"the {fileSystem.LvmVolumeGroup} LVM volume group ({FormatBytes(freed)} returned)";
        var warnings = new List<string>
        {
            "Shrinking storage can cause data loss if power is lost or the storage layout changes.",
            "A current backup must be confirmed before execution.",
            $"{FormatBytes(freed)} will be returned to {destination}."
        };
        var plan = new StorageResizePlan(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            fileSystem.Id,
            fileSystem.MountPoint,
            fileSystem.DevicePath,
            fileSystem.DiskDevicePath ?? string.Empty,
            fileSystem.FileSystemType,
            StorageResizeDirection.Shrink,
            fileSystem.CurrentSizeBytes,
            containerTarget,
            minimum,
            fileSystem.CurrentSizeBytes,
            true,
            false,
            false,
            StorageResizeRisk.High,
            false,
            topology.Fingerprint,
            destination,
            steps,
            warnings,
            BuildInitialValidations(fileSystem, containerTarget, topology));
        var fullValidation = await ValidatePlanAsync(plan, cancellationToken);
        return plan with { ValidationResults = fullValidation };
    }

    private async Task<StorageValidationResult> CheckMdRaidIdleAsync(CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "cat",
                ["/proc/mdstat"],
                RequiresSudo: false,
                TimeSpan.FromSeconds(5),
                "Check for active software RAID rebuilds")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new StorageValidationResult(
                "Software RAID",
                true,
                "No software RAID status was available.",
                StorageValidationSeverity.Information);
        }

        var busy = result.StandardOutput.Contains("resync", StringComparison.OrdinalIgnoreCase) ||
                   result.StandardOutput.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
                   result.StandardOutput.Contains("reshape", StringComparison.OrdinalIgnoreCase) ||
                   (result.StandardOutput.Contains("check", StringComparison.OrdinalIgnoreCase) &&
                    result.StandardOutput.Contains("=", StringComparison.Ordinal));
        return new StorageValidationResult(
            "Software RAID",
            !busy,
            busy
                ? "An mdadm rebuild, reshape or check is active. Wait for it to finish before resizing."
                : "No active mdadm rebuild or reshape was detected.");
    }

    private async Task<StorageValidationResult> CheckLvmConflictsAsync(
        StorageFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileSystem.LvmVolumeGroup))
        {
            return new StorageValidationResult("LVM conflicts", true, "This filesystem is not LVM-backed.");
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "lvs",
                ["--reportformat", "json", "--units", "b", "--nosuffix", "-o", "lv_name,vg_name,lv_attr,lv_path"],
                RequiresSudo: false,
                TimeSpan.FromSeconds(10),
                "Check for conflicting LVM operations")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return new StorageValidationResult(
                "LVM conflicts",
                false,
                "LMS could not verify that LVM is idle before resizing.",
                StorageValidationSeverity.Warning);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("report", out var reports) || reports.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return new StorageValidationResult("LVM conflicts", true, "LVM reported no conflicting volume state.");
            }

            foreach (var report in reports.EnumerateArray())
            {
                if (!report.TryGetProperty("lv", out var rows) || rows.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var row in rows.EnumerateArray())
                {
                    var vg = row.TryGetProperty("vg_name", out var vgValue) ? vgValue.GetString() ?? string.Empty : string.Empty;
                    if (!vg.Equals(fileSystem.LvmVolumeGroup, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var attr = row.TryGetProperty("lv_attr", out var attrValue) ? attrValue.GetString() ?? string.Empty : string.Empty;
                    // lv_attr[0]=volume type (s=snapshot), [5]=state (m=merging), [8]=health (r=refresh needed).
                    if (attr.Length >= 6 && (attr[0] == 's' && attr[5] == 'm' || attr[5] == 'm'))
                    {
                        var name = row.TryGetProperty("lv_name", out var nameValue) ? nameValue.GetString() : "volume";
                        return new StorageValidationResult(
                            "LVM conflicts",
                            false,
                            $"LVM snapshot merge is in progress on {name}. Wait for it to finish before resizing.");
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return new StorageValidationResult(
                "LVM conflicts",
                false,
                "LVM returned an unfamiliar status payload.",
                StorageValidationSeverity.Warning);
        }

        return new StorageValidationResult("LVM conflicts", true, "No LVM snapshot merge is in progress for this volume group.");
    }

    private async Task<Ext4Inspection> InspectExt4Async(
        string devicePath,
        long usedBytes,
        CancellationToken cancellationToken)
    {
        if (!IsSafeDevicePath(devicePath))
        {
            return new Ext4Inspection(false, 0, 0, "The EXT4 device path is not safe to inspect.");
        }

        var minimumTask = commandRunner.RunAsync(
            new LinuxCommandRequest(
                "resize2fs",
                ["-P", devicePath],
                RequiresSudo: true,
                InspectionTimeout,
                $"Calculate the minimum EXT4 size for {devicePath}"),
            dryRun: false,
            cancellationToken);
        var metadataTask = commandRunner.RunAsync(
            new LinuxCommandRequest(
                "tune2fs",
                ["-l", devicePath],
                RequiresSudo: true,
                InspectionTimeout,
                $"Inspect EXT4 metadata for {devicePath}"),
            dryRun: false,
            cancellationToken);
        await Task.WhenAll(minimumTask, metadataTask);

        var minimumResult = minimumTask.Result;
        var metadataResult = metadataTask.Result;
        if (minimumResult.ExitCode != 0 || metadataResult.ExitCode != 0)
        {
            return new Ext4Inspection(false, 0, 0, "LMS could not calculate and verify the EXT4 minimum size. No shrink plan was created.");
        }

        var blocksMatch = MinimumBlocksRegex().Match(minimumResult.StandardOutput);
        var blockSizeMatch = BlockSizeRegex().Match(metadataResult.StandardOutput);
        if (!long.TryParse(blocksMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blocks) ||
            !long.TryParse(blockSizeMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockSize) ||
            blocks <= 0 || blockSize <= 0)
        {
            return new Ext4Inspection(false, 0, 0, "EXT4 returned an unfamiliar minimum-size result. No shrink plan was created.");
        }

        var stateMatch = FileSystemStateRegex().Match(metadataResult.StandardOutput);
        var state = stateMatch.Success ? stateMatch.Groups[1].Value.Trim() : "unknown";
        if (state.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return new Ext4Inspection(false, blockSize, 0, $"EXT4 reports filesystem state '{state}'. Repair it before shrinking.");
        }

        var calculated = checked(blocks * blockSize);
        var dataHeadroom = Math.Max(2 * GiB, (long)Math.Ceiling(usedBytes * 0.15));
        var lmsMinimum = AlignUp(Math.Max(checked(calculated + calculated / 10), checked(usedBytes + dataHeadroom)), 4 * MiB);
        return new Ext4Inspection(true, blockSize, lmsMinimum, $"EXT4 metadata is readable and reports state '{state}'.");
    }

    private static IReadOnlyList<StorageValidationResult> BuildInitialValidations(
        StorageFileSystem fileSystem,
        long target,
        StorageTopologySnapshot topology) =>
    [
        new("Disk visible", !string.IsNullOrWhiteSpace(fileSystem.DiskDevicePath), fileSystem.DiskDevicePath ?? "No disk mapped."),
        new("Filesystem visible", true, $"{fileSystem.DevicePath} → {fileSystem.MountPoint}"),
        new("Filesystem supported", fileSystem.FileSystemType is "ext4" or "xfs", fileSystem.FileSystemType.ToUpperInvariant()),
        new("Target within range", target >= fileSystem.MinimumSafeSizeBytes && target <= fileSystem.MaximumSizeBytes,
            $"{FormatBytes(target)} requested."),
        new("Storage topology captured", !string.IsNullOrWhiteSpace(topology.Fingerprint), "The topology will be checked again immediately before execution.")
    ];

    private static void EnsurePartitionIdentity(StorageFileSystem fileSystem)
    {
        if (string.IsNullOrWhiteSpace(fileSystem.DiskDevicePath) ||
            string.IsNullOrWhiteSpace(fileSystem.PartitionDevicePath) ||
            !fileSystem.PartitionNumber.HasValue ||
            !IsSafeDevicePath(fileSystem.DiskDevicePath) ||
            !IsSafeDevicePath(fileSystem.PartitionDevicePath))
        {
            throw new InvalidOperationException("LMS could not safely identify the disk and partition number required for this resize.");
        }
    }

    private static IReadOnlyList<string> BuildMountArguments(StorageFileSystem fileSystem)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(fileSystem.MountOptions))
        {
            arguments.Add("-o");
            arguments.Add(fileSystem.MountOptions);
        }

        arguments.Add("--");
        arguments.Add(fileSystem.DevicePath);
        arguments.Add(fileSystem.MountPoint);
        return arguments;
    }

    private static void AddCommand(
        ICollection<StorageResizeStep> steps,
        string title,
        string description,
        StorageResizeStepOperation operation,
        string executable,
        IReadOnlyList<string> arguments,
        bool mutation)
    {
        steps.Add(new StorageResizeStep(
            steps.Count + 1,
            title,
            description,
            operation,
            executable,
            arguments,
            RenderCommand(executable, arguments),
            mutation));
    }

    private static void AddVerification(ICollection<StorageResizeStep> steps, StorageFileSystem fileSystem, long target)
    {
        steps.Add(new StorageResizeStep(
            steps.Count + 1,
            "Verify the result",
            $"Rediscover storage and confirm {fileSystem.MountPoint} is approximately {FormatBytes(target)}.",
            StorageResizeStepOperation.Verify,
            null,
            [],
            "LMS topology verification",
            false));
    }

    internal static string RenderCommand(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments.Select(QuoteArgument)));

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character => char.IsLetterOrDigit(character) || character is '/' or '.' or '-' or '_' or ':' or '+' or '='))
        {
            return argument;
        }

        return $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static long AlignDown(long value, long alignment) =>
        alignment <= 0 ? value : value - value % alignment;

    private static long AlignUp(long value, long alignment)
    {
        if (alignment <= 0) return value;
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static bool IsSafeDevicePath(string path) =>
        path.StartsWith("/dev/", StringComparison.Ordinal) &&
        path.Skip(5).All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/');

    private static string FormatBytes(long bytes)
    {
        var gib = bytes / (double)GiB;
        return gib >= 1024 ? $"{gib / 1024:0.##} TB" : $"{gib:0.##} GB";
    }

    [GeneratedRegex(@"Estimated minimum size of the filesystem:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinimumBlocksRegex();

    [GeneratedRegex(@"^Block size:\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BlockSizeRegex();

    [GeneratedRegex(@"^Filesystem state:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FileSystemStateRegex();

    private sealed record Ext4Inspection(bool Success, long BlockSizeBytes, long LmsMinimumBytes, string Detail);
}
