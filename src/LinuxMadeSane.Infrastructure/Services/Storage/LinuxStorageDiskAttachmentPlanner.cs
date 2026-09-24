// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Text;
using LinuxMadeSane.Application.Contracts.Storage;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Infrastructure.Services.Storage;

public sealed class LinuxStorageDiskAttachmentPlanner(
    IStorageDiscoveryService discoveryService,
    ILinuxCommandRunner commandRunner,
    TimeProvider timeProvider) : IStorageDiskAttachmentPlanner
{
    private static readonly HashSet<string> ReservedMountPoints = new(StringComparer.Ordinal)
    {
        "/", "/boot", "/boot/efi", "/efi", "/dev", "/proc", "/sys", "/run", "/tmp", "/var/run"
    };

    public async Task<StorageResizePlan> CreateMountPlanAsync(
        StorageAttachMountRequest request,
        CancellationToken cancellationToken = default)
    {
        var topology = await discoveryService.DiscoverAsync(cancellationToken);
        var disk = RequireNewDisk(topology, request.DiskDevicePath);
        var mountPoint = NormalizeMountPoint(request.MountPoint);
        EnsureMountPointAllowed(mountPoint);
        EnsureMountPointEmptyOrMissing(mountPoint);

        var storageDisk = topology.Disks.Single(item => item.DevicePath == disk.DevicePath);
        var partitionPath = ResolvePartitionPath(disk, storageDisk);
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? BuildLabel(mountPoint)
            : SanitizeLabel(request.Label);
        var steps = new List<StorageResizeStep>();

        if (!disk.HasExistingPartitions)
        {
            AddCommand(
                steps,
                "Create a GPT partition table",
                $"Prepare {disk.DevicePath} with a modern GPT layout.",
                StorageResizeStepOperation.CreatePartitionTable,
                "parted",
                ["-s", disk.DevicePath, "mklabel", "gpt"],
                mutation: true);
            AddCommand(
                steps,
                "Create a data partition",
                "Use the whole disk for one filesystem partition.",
                StorageResizeStepOperation.CreatePartition,
                "parted",
                ["-s", "-a", "optimal", disk.DevicePath, "mkpart", "primary", "ext4", "1MiB", "100%"],
                mutation: true);
            partitionPath = GuessFirstPartitionPath(disk.DevicePath);
        }

        AddCommand(
            steps,
            "Refresh device nodes",
            "Ask the kernel to expose the new partition before formatting.",
            StorageResizeStepOperation.SettleDevices,
            "partprobe",
            [disk.DevicePath],
            mutation: false);
        AddCommand(
            steps,
            "Format as EXT4",
            $"Create a clean EXT4 filesystem labelled {label}.",
            StorageResizeStepOperation.FormatExt4,
            "mkfs.ext4",
            ["-F", "-L", label, partitionPath],
            mutation: true);
        AddCommand(
            steps,
            "Create the mount folder",
            $"Ensure {mountPoint} exists and is empty.",
            StorageResizeStepOperation.CreateDirectory,
            "mkdir",
            ["-p", "--", mountPoint],
            mutation: false);
        AddCommand(
            steps,
            "Mount the new disk",
            $"Attach {partitionPath} at {mountPoint}.",
            StorageResizeStepOperation.Mount,
            "mount",
            ["--", partitionPath, mountPoint],
            mutation: false);
        steps.Add(new StorageResizeStep(
            steps.Count + 1,
            "Remember the mount after reboot",
            $"Add a UUID-based /etc/fstab entry for {mountPoint}.",
            StorageResizeStepOperation.UpdateFstab,
            null,
            [partitionPath, mountPoint, "ext4", "defaults,nofail", "0", "2"],
            $"fstab UUID=<detected> {mountPoint} ext4 defaults,nofail 0 2",
            true));
        AddVerification(steps, mountPoint, disk.DevicePath);

        var plan = new StorageResizePlan(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            $"newdisk:{NormalizeDeviceKey(disk.DevicePath)}",
            mountPoint,
            partitionPath,
            disk.DevicePath,
            "ext4",
            StorageResizeDirection.AttachMount,
            0,
            disk.SizeBytes,
            0,
            disk.SizeBytes,
            false,
            false,
            false,
            StorageResizeRisk.Medium,
            false,
            topology.Fingerprint,
            string.Empty,
            steps,
            [
                "A current backup is recommended before modifying storage.",
                $"{FormatBytes(disk.SizeBytes)} on {disk.DevicePath} will become {mountPoint}.",
                "Existing data on this unused disk will be erased when it is formatted."
            ],
            [
                new StorageValidationResult("New disk selected", true, disk.Detail),
                new StorageValidationResult("Mount point", true, mountPoint)
            ]);
        return plan with { ValidationResults = await ValidatePlanAsync(plan, cancellationToken) };
    }

    public async Task<StorageResizePlan> CreateVolumeGroupPlanAsync(
        StorageAttachVolumeGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var topology = await discoveryService.DiscoverAsync(cancellationToken);
        var disk = RequireNewDisk(topology, request.DiskDevicePath);
        var volumeGroup = topology.VolumeGroups.SingleOrDefault(group =>
                             group.Name.Equals(request.VolumeGroupName, StringComparison.Ordinal))
                         ?? throw new InvalidOperationException($"Volume group '{request.VolumeGroupName}' was not found.");

        var storageDisk = topology.Disks.Single(item => item.DevicePath == disk.DevicePath);
        var partitionPath = ResolvePartitionPath(disk, storageDisk);
        var steps = new List<StorageResizeStep>();
        if (!disk.HasExistingPartitions)
        {
            AddCommand(
                steps,
                "Create a GPT partition table",
                $"Prepare {disk.DevicePath} for LVM.",
                StorageResizeStepOperation.CreatePartitionTable,
                "parted",
                ["-s", disk.DevicePath, "mklabel", "gpt"],
                mutation: true);
            AddCommand(
                steps,
                "Create an LVM partition",
                "Use the whole disk as one LVM physical volume partition.",
                StorageResizeStepOperation.CreatePartition,
                "parted",
                ["-s", "-a", "optimal", disk.DevicePath, "mkpart", "primary", "1MiB", "100%"],
                mutation: true);
            AddCommand(
                steps,
                "Mark the partition as LVM",
                "Set the GPT partition type so Linux treats it as an LVM physical volume.",
                StorageResizeStepOperation.CreatePartition,
                "parted",
                ["-s", disk.DevicePath, "set", "1", "lvm", "on"],
                mutation: true);
            partitionPath = GuessFirstPartitionPath(disk.DevicePath);
        }

        AddCommand(
            steps,
            "Refresh device nodes",
            "Ask the kernel to expose the new partition before creating the physical volume.",
            StorageResizeStepOperation.SettleDevices,
            "partprobe",
            [disk.DevicePath],
            mutation: false);
        AddCommand(
            steps,
            "Create an LVM physical volume",
            $"Initialise {partitionPath} for LVM.",
            StorageResizeStepOperation.PvCreate,
            "pvcreate",
            ["--yes", "--force", partitionPath],
            mutation: true);
        AddCommand(
            steps,
            $"Add the disk to {volumeGroup.Name}",
            $"Extend the {volumeGroup.Name} storage pool with the new capacity.",
            StorageResizeStepOperation.VgExtend,
            "vgextend",
            [volumeGroup.Name, partitionPath],
            mutation: true);
        AddVerification(steps, volumeGroup.Name, disk.DevicePath);

        var plan = new StorageResizePlan(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            $"newdisk:{NormalizeDeviceKey(disk.DevicePath)}",
            volumeGroup.Name,
            partitionPath,
            disk.DevicePath,
            "lvm",
            StorageResizeDirection.AttachToVolumeGroup,
            volumeGroup.SizeBytes,
            checked(volumeGroup.SizeBytes + disk.SizeBytes),
            volumeGroup.SizeBytes,
            checked(volumeGroup.SizeBytes + disk.SizeBytes),
            false,
            false,
            false,
            StorageResizeRisk.Medium,
            false,
            topology.Fingerprint,
            $"{volumeGroup.Name} LVM volume group",
            steps,
            [
                "A current backup is recommended before modifying storage.",
                $"{FormatBytes(disk.SizeBytes)} will be added to {volumeGroup.Name}.",
                "After this completes, use Resize on a filesystem to claim the new pool space.",
                "Existing data on this unused disk will be erased."
            ],
            [
                new StorageValidationResult("New disk selected", true, disk.Detail),
                new StorageValidationResult("Volume group", true, volumeGroup.Name)
            ]);
        return plan with { ValidationResults = await ValidatePlanAsync(plan, cancellationToken) };
    }

    public async Task<IReadOnlyList<StorageValidationResult>> ValidatePlanAsync(
        StorageResizePlan plan,
        CancellationToken cancellationToken = default)
    {
        var results = new List<StorageValidationResult>();
        var topology = await discoveryService.DiscoverAsync(cancellationToken);
        var disk = topology.NewDisks.FirstOrDefault(item => item.DevicePath == plan.TargetDiskDevicePath);
        results.Add(new StorageValidationResult(
            "Disk still unused",
            disk is not null,
            disk is null
                ? "The selected disk is no longer unused. Refresh Storage and choose again."
                : disk.Detail));
        results.Add(new StorageValidationResult(
            "Storage topology unchanged",
            topology.Fingerprint.Equals(plan.TopologyFingerprint, StringComparison.Ordinal),
            topology.Fingerprint.Equals(plan.TopologyFingerprint, StringComparison.Ordinal)
                ? "Disks and mounts still match this plan."
                : "Storage changed after this plan was created. Create a fresh plan."));

        if (plan.OperationType == StorageResizeDirection.AttachMount)
        {
            results.Add(new StorageValidationResult(
                "Mount point available",
                IsMountPointEmptyOrMissing(plan.TargetMountPoint),
                IsMountPointEmptyOrMissing(plan.TargetMountPoint)
                    ? $"{plan.TargetMountPoint} is empty or does not exist yet."
                    : $"{plan.TargetMountPoint} already contains files. Choose an empty folder."));
            results.Add(new StorageValidationResult(
                "Mount point not already mounted",
                topology.FileSystems.All(item => !item.MountPoint.Equals(plan.TargetMountPoint, StringComparison.Ordinal)),
                $"Mount point {plan.TargetMountPoint}."));
        }

        if (plan.OperationType == StorageResizeDirection.AttachToVolumeGroup)
        {
            results.Add(new StorageValidationResult(
                "Volume group present",
                topology.VolumeGroups.Any(group => group.Name.Equals(plan.TargetMountPoint, StringComparison.Ordinal)),
                plan.TargetMountPoint));
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
                probe.ExitCode == 0 ? $"{executable} is installed." : $"{executable} is required for this plan."));
        }

        return results;
    }

    private static StorageNewDisk RequireNewDisk(StorageTopologySnapshot topology, string diskDevicePath)
    {
        if (!IsSafeDevicePath(diskDevicePath))
        {
            throw new InvalidOperationException("The disk path is not safe to use.");
        }

        return topology.NewDisks.SingleOrDefault(disk => disk.DevicePath == diskDevicePath)
               ?? throw new InvalidOperationException("That disk is not currently an unused new-disk candidate. Refresh Storage and try again.");
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        var trimmed = (mountPoint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.StartsWith('/'))
        {
            throw new InvalidOperationException("Choose an absolute folder path such as /mnt/data.");
        }

        var full = Path.GetFullPath(trimmed);
        if (!full.StartsWith('/'))
        {
            throw new InvalidOperationException("Choose an absolute folder path such as /mnt/data.");
        }

        return full is "/" ? "/" : full.TrimEnd('/');
    }

    private static void EnsureMountPointAllowed(string mountPoint)
    {
        if (ReservedMountPoints.Contains(mountPoint) ||
            mountPoint.StartsWith("/boot/", StringComparison.Ordinal) ||
            mountPoint.StartsWith("/dev/", StringComparison.Ordinal) ||
            mountPoint.StartsWith("/proc/", StringComparison.Ordinal) ||
            mountPoint.StartsWith("/sys/", StringComparison.Ordinal) ||
            mountPoint.StartsWith("/run/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{mountPoint} is reserved by the operating system.");
        }
    }

    private static void EnsureMountPointEmptyOrMissing(string mountPoint)
    {
        if (!IsMountPointEmptyOrMissing(mountPoint))
        {
            throw new InvalidOperationException(
                $"{mountPoint} already contains files. Choose an empty folder (for example /mnt/data) or move the contents aside first. Automatic folder migration is coming next.");
        }
    }

    private static bool IsMountPointEmptyOrMissing(string mountPoint)
    {
        if (!Directory.Exists(mountPoint))
        {
            return true;
        }

        return !Directory.EnumerateFileSystemEntries(mountPoint).Any();
    }

    private static string ResolvePartitionPath(StorageNewDisk disk, StorageDisk storageDisk)
    {
        if (!disk.HasExistingPartitions)
        {
            return GuessFirstPartitionPath(disk.DevicePath);
        }

        var partition = storageDisk.Nodes
            .Where(node => node.Kind == StorageNodeKind.Partition)
            .OrderBy(node => node.PartitionNumber ?? int.MaxValue)
            .ThenBy(node => node.StartSector ?? long.MaxValue)
            .FirstOrDefault();
        return partition?.DevicePath ?? GuessFirstPartitionPath(disk.DevicePath);
    }

    private static string GuessFirstPartitionPath(string diskPath)
    {
        var name = Path.GetFileName(diskPath);
        return name.StartsWith("nvme", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("mmcblk", StringComparison.OrdinalIgnoreCase)
            ? $"{diskPath}p1"
            : $"{diskPath}1";
    }

    private static string BuildLabel(string mountPoint)
    {
        var leaf = mountPoint.Trim('/').Replace('/', '-');
        if (string.IsNullOrWhiteSpace(leaf)) leaf = "lms-data";
        return SanitizeLabel(leaf);
    }

    private static string SanitizeLabel(string label)
    {
        var builder = new StringBuilder();
        foreach (var character in label)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
            }
            else if (character is '/' or ' ')
            {
                builder.Append('-');
            }
        }

        var clean = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(clean)) clean = "lms-data";
        return clean.Length <= 16 ? clean : clean[..16];
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
            LinuxStorageResizePlanner.RenderCommand(executable, arguments),
            mutation));
    }

    private static void AddVerification(ICollection<StorageResizeStep> steps, string target, string diskPath)
    {
        steps.Add(new StorageResizeStep(
            steps.Count + 1,
            "Verify the result",
            $"Confirm {target} is using {diskPath}.",
            StorageResizeStepOperation.Verify,
            null,
            [],
            "LMS topology verification",
            false));
    }

    private static bool IsSafeDevicePath(string path) =>
        path.StartsWith("/dev/", StringComparison.Ordinal) &&
        path.Skip(5).All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or '/');

    private static string NormalizeDeviceKey(string path) =>
        path.StartsWith("/dev/", StringComparison.Ordinal) ? path["/dev/".Length..] : path;

    private static string FormatBytes(long bytes)
    {
        var gib = bytes / (1024d * 1024d * 1024d);
        return gib >= 1024 ? $"{gib / 1024:0.##} TB" : $"{gib:0.##} GB";
    }
}
