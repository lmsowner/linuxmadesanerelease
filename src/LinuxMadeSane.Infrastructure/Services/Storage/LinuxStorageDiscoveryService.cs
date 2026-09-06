// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Storage;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
using Microsoft.Extensions.Logging;

namespace LinuxMadeSane.Infrastructure.Services.Storage;

public sealed class LinuxStorageDiscoveryService(
    ILinuxCommandRunner commandRunner,
    ILogger<LinuxStorageDiscoveryService> logger,
    TimeProvider timeProvider) : IStorageDiscoveryService
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(20);

    public async Task<StorageTopologySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var lsblkResult = await RunAsync(
            "lsblk",
            ["--bytes", "--json", "--output", "NAME,KNAME,PATH,PKNAME,TYPE,SIZE,FSTYPE,FSVER,LABEL,UUID,MOUNTPOINTS,FSAVAIL,FSUSED,FSUSE%,RO,RM,MODEL,SERIAL,TRAN,LOG-SEC,PHY-SEC,START,PARTN"],
            "Inspect block device topology",
            optional: false,
            cancellationToken);
        if (lsblkResult.ExitCode != 0 || string.IsNullOrWhiteSpace(lsblkResult.StandardOutput))
        {
            throw new InvalidOperationException(BuildFailure("lsblk could not inspect this host", lsblkResult));
        }

        var findmntTask = RunAsync(
            "findmnt",
            ["--json", "--bytes", "--real", "--output", "SOURCE,TARGET,FSTYPE,SIZE,USED,AVAIL,OPTIONS"],
            "Inspect mounted filesystems",
            optional: false,
            cancellationToken);
        var pvsTask = RunAsync(
            "pvs",
            ["--reportformat", "json", "--units", "b", "--nosuffix", "-o", "pv_name,vg_name,pv_size,pv_free"],
            "Inspect LVM physical volumes",
            optional: true,
            cancellationToken);
        var vgsTask = RunAsync(
            "vgs",
            ["--reportformat", "json", "--units", "b", "--nosuffix", "-o", "vg_name,vg_size,vg_free,vg_extent_size"],
            "Inspect LVM volume groups",
            optional: true,
            cancellationToken);
        var lvsTask = RunAsync(
            "lvs",
            ["--reportformat", "json", "--units", "b", "--nosuffix", "-o", "lv_path,lv_dm_path,vg_name,lv_name,lv_size,lv_attr"],
            "Inspect LVM logical volumes",
            optional: true,
            cancellationToken);

        await Task.WhenAll(findmntTask, pvsTask, vgsTask, lvsTask);

        var warnings = new List<string>();
        if (findmntTask.Result.ExitCode != 0)
        {
            warnings.Add("Mounted filesystem sizes could not be read. Resize remains unavailable until findmnt succeeds.");
        }

        var rawDevices = ParseLsblk(lsblkResult.StandardOutput);
        var mounts = findmntTask.Result.ExitCode == 0
            ? ParseFindmnt(findmntTask.Result.StandardOutput)
            : [];
        var pvs = pvsTask.Result.ExitCode == 0 ? ParseLvmRows(pvsTask.Result.StandardOutput, "pv") : [];
        var vgs = vgsTask.Result.ExitCode == 0 ? ParseLvmRows(vgsTask.Result.StandardOutput, "vg") : [];
        var lvs = lvsTask.Result.ExitCode == 0 ? ParseLvmRows(lvsTask.Result.StandardOutput, "lv") : [];
        var fileSystemSizes = await InspectFileSystemSizesAsync(rawDevices, mounts, cancellationToken);

        var topology = BuildTopology(rawDevices, mounts, pvs, vgs, lvs, fileSystemSizes, warnings, timeProvider.GetUtcNow());
        var disksWithHealth = await Task.WhenAll(topology.Disks.Select(disk => ReadDiskHealthAsync(disk, cancellationToken)));
        return topology with
        {
            Disks = disksWithHealth,
            Fingerprint = CreateFingerprint(disksWithHealth, topology.FileSystems)
        };
    }

    public async Task<StorageTopologySnapshot> RescanAsync(CancellationToken cancellationToken = default)
    {
        var current = await DiscoverAsync(cancellationToken);
        foreach (var disk in current.Disks.Where(disk => IsSafeDevicePath(disk.DevicePath)))
        {
            var deviceName = Path.GetFileName(disk.DevicePath);
            var rescanPath = $"/sys/class/block/{deviceName}/device/rescan";
            if (File.Exists(rescanPath))
            {
                var result = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "tee",
                        [rescanPath],
                        RequiresSudo: true,
                        DiscoveryTimeout,
                        $"Rescan {disk.DevicePath}")
                    {
                        StandardInputBytes = "1\n"u8.ToArray(),
                        IsOptionalExternalTool = true
                    },
                    dryRun: false,
                    cancellationToken);
                if (result.ExitCode != 0)
                {
                    logger.LogDebug("Storage rescan was not available for {Disk}: {Error}", disk.DevicePath, result.StandardError);
                }
            }
        }

        return await DiscoverAsync(cancellationToken);
    }

    internal static StorageTopologySnapshot BuildTopology(
        IReadOnlyList<RawBlockDevice> roots,
        IReadOnlyList<RawMount> mounts,
        IReadOnlyList<IReadOnlyDictionary<string, string>> pvs,
        IReadOnlyList<IReadOnlyDictionary<string, string>> vgs,
        IReadOnlyList<IReadOnlyDictionary<string, string>> lvs,
        IReadOnlyDictionary<string, long> fileSystemSizes,
        ICollection<string> warnings,
        DateTimeOffset capturedUtc)
    {
        var flattened = Flatten(roots).ToArray();
        var mountLookup = mounts
            .Where(mount => mount.Source.StartsWith("/dev/", StringComparison.Ordinal))
            .GroupBy(mount => NormalizeDeviceKey(mount.Source), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var volumeGroups = vgs
            .Where(row => !string.IsNullOrWhiteSpace(Get(row, "vg_name")))
            .ToDictionary(row => Get(row, "vg_name"), StringComparer.Ordinal);
        var logicalVolumes = lvs
            .Select(row => new
            {
                Row = row,
                Paths = new[] { Get(row, "lv_path"), Get(row, "lv_dm_path") }
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeDeviceKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            })
            .SelectMany(item => item.Paths.Select(path => new { Path = path, item.Row }))
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.Ordinal);
        var physicalVolumes = pvs
            .Where(row => !string.IsNullOrWhiteSpace(Get(row, "pv_name")))
            .GroupBy(row => NormalizeDeviceKey(Get(row, "pv_name")), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var disks = new List<StorageDisk>();
        var filesystems = new List<StorageFileSystem>();

        foreach (var root in roots.Where(device => device.Type.Equals("disk", StringComparison.OrdinalIgnoreCase)))
        {
            var diskDescendants = Flatten([root]).ToArray();
            var sectorSize = root.LogicalSectorSize > 0 ? root.LogicalSectorSize : 512;
            var partitionEnds = diskDescendants
                .Where(device => device.Type.Equals("part", StringComparison.OrdinalIgnoreCase))
                .Select(device => checked((device.StartSector ?? 0) * sectorSize + device.SizeBytes))
                .ToArray();
            var allocatedBytes = partitionEnds.Length == 0 ? 0 : Math.Min(root.SizeBytes, partitionEnds.Max());
            var unallocatedBytes = Math.Max(0, root.SizeBytes - allocatedBytes - MiB);
            var nodes = new List<StorageNode>();

            foreach (var device in diskDescendants)
            {
                if (ReferenceEquals(device, root))
                {
                    continue;
                }

                var kind = MapKind(device.Type);
                var deviceKey = NormalizeDeviceKey(device.Path);
                mountLookup.TryGetValue(deviceKey, out var mount);
                logicalVolumes.TryGetValue(deviceKey, out var logicalVolume);
                var volumeGroupName = logicalVolume is null ? string.Empty : Get(logicalVolume, "vg_name");
                volumeGroups.TryGetValue(volumeGroupName, out var volumeGroup);
                var parentId = device.Parent is null || ReferenceEquals(device.Parent, root) ? null : BuildNodeId(device.Parent);
                if (logicalVolume is not null && !string.IsNullOrWhiteSpace(volumeGroupName))
                {
                    var partitionAncestor = Ancestors(device)
                        .FirstOrDefault(item => item.Type.Equals("part", StringComparison.OrdinalIgnoreCase));
                    if (partitionAncestor is not null &&
                        physicalVolumes.TryGetValue(NormalizeDeviceKey(partitionAncestor.Path), out var physicalVolume))
                    {
                        var pvId = $"pv:{NormalizeDeviceKey(partitionAncestor.Path)}";
                        if (nodes.All(node => node.Id != pvId))
                        {
                            nodes.Add(new StorageNode(
                                pvId,
                                BuildNodeId(partitionAncestor),
                                StorageNodeKind.LvmPhysicalVolume,
                                $"LVM physical volume {partitionAncestor.Path}",
                                partitionAncestor.Path,
                                ParseLong(Get(physicalVolume, "pv_size")),
                                VolumeGroup: volumeGroupName,
                                VolumeGroupFreeBytes: ParseLong(Get(physicalVolume, "pv_free"))));
                        }

                        var vgId = $"vg:{volumeGroupName}";
                        if (nodes.All(node => node.Id != vgId))
                        {
                            nodes.Add(new StorageNode(
                                vgId,
                                pvId,
                                StorageNodeKind.LvmVolumeGroup,
                                $"LVM volume group {volumeGroupName}",
                                volumeGroupName,
                                volumeGroup is null ? ParseLong(Get(physicalVolume, "pv_size")) : ParseLong(Get(volumeGroup, "vg_size")),
                                VolumeGroup: volumeGroupName,
                                VolumeGroupFreeBytes: volumeGroup is null ? ParseLong(Get(physicalVolume, "pv_free")) : ParseLong(Get(volumeGroup, "vg_free"))));
                        }

                        parentId = vgId;
                    }
                }
                nodes.Add(new StorageNode(
                    BuildNodeId(device),
                    parentId,
                    kind,
                    device.Name,
                    device.Path,
                    device.SizeBytes,
                    mount?.FileSystemType ?? device.FileSystemType,
                    mount?.Target ?? device.MountPoints.FirstOrDefault(point => !string.IsNullOrWhiteSpace(point)),
                    mount?.UsedBytes ?? device.FileSystemUsedBytes,
                    mount?.AvailableBytes ?? device.FileSystemAvailableBytes,
                    device.PartitionNumber,
                    device.StartSector,
                    device.LogicalSectorSize > 0 ? device.LogicalSectorSize : sectorSize,
                    string.IsNullOrWhiteSpace(volumeGroupName) ? null : volumeGroupName,
                    volumeGroup is null ? null : ParseLong(Get(volumeGroup, "vg_free")),
                    device.ReadOnly));
            }

            var storageDisk = new StorageDisk(
                BuildNodeId(root),
                root.Path,
                root.Name,
                string.IsNullOrWhiteSpace(root.Model) ? "Linux block device" : root.Model.Trim(),
                string.IsNullOrWhiteSpace(root.Transport) ? "Unknown" : root.Transport,
                root.SizeBytes,
                allocatedBytes,
                unallocatedBytes,
                sectorSize,
                root.PhysicalSectorSize > 0 ? root.PhysicalSectorSize : sectorSize,
                root.Removable,
                "Unknown",
                null,
                null,
                nodes);
            disks.Add(storageDisk);

            foreach (var device in diskDescendants.Where(device => HasSupportedMountedFileSystem(device, mountLookup)))
            {
                var fileSystem = BuildFileSystem(
                    device,
                    root,
                    storageDisk,
                    mountLookup,
                    physicalVolumes,
                    volumeGroups,
                    logicalVolumes,
                    fileSystemSizes);
                filesystems.Add(fileSystem);
            }
        }

        foreach (var mountedLocal in mounts.Where(mount => mount.Source.StartsWith("/dev/", StringComparison.Ordinal)))
        {
            if (filesystems.Any(fileSystem =>
                    fileSystem.MountPoint.Equals(mountedLocal.Target, StringComparison.Ordinal) ||
                    NormalizeDeviceKey(fileSystem.DevicePath).Equals(NormalizeDeviceKey(mountedLocal.Source), StringComparison.Ordinal)))
            {
                continue;
            }

            warnings.Add($"{mountedLocal.Target} uses a storage layout LMS could not map to a physical disk. It is inspect-only.");
            filesystems.Add(new StorageFileSystem(
                $"fs:{NormalizeDeviceKey(mountedLocal.Source)}:{mountedLocal.Target}",
                mountedLocal.Source,
                mountedLocal.Target,
                mountedLocal.FileSystemType,
                mountedLocal.SizeBytes,
                mountedLocal.UsedBytes,
                mountedLocal.AvailableBytes,
                mountedLocal.SizeBytes,
                mountedLocal.SizeBytes,
                false,
                false,
                "LMS could not safely map every layer between this filesystem and its disk.",
                null,
                null,
                null,
                null,
                null,
                null,
                mountedLocal.SizeBytes,
                0,
                0,
                0,
                0,
                false,
                mountedLocal.Target == "/",
                false,
                mountedLocal.Options,
                [mountedLocal.Source, $"{mountedLocal.FileSystemType} → {mountedLocal.Target}"]));
        }

        var fingerprint = CreateFingerprint(disks, filesystems);
        var volumeGroupModels = BuildVolumeGroups(vgs, pvs);
        var newDisks = BuildNewDisks(disks, filesystems, physicalVolumes, volumeGroupModels);
        disks = disks
            .Select(disk =>
            {
                var candidate = newDisks.FirstOrDefault(item => item.DevicePath == disk.DevicePath);
                return candidate is null
                    ? disk
                    : disk with
                    {
                        IsNewDiskCandidate = true,
                        NewDiskDetail = candidate.Detail
                    };
            })
            .ToList();
        return new StorageTopologySnapshot(
            capturedUtc,
            fingerprint,
            disks.OrderBy(disk => disk.DevicePath, StringComparer.Ordinal).ToArray(),
            filesystems.OrderBy(fileSystem => fileSystem.MountPoint, StringComparer.Ordinal).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            newDisks,
            volumeGroupModels);
    }

    private static IReadOnlyList<StorageVolumeGroup> BuildVolumeGroups(
        IReadOnlyList<IReadOnlyDictionary<string, string>> vgs,
        IReadOnlyList<IReadOnlyDictionary<string, string>> pvs) =>
        vgs
            .Where(row => !string.IsNullOrWhiteSpace(Get(row, "vg_name")))
            .Select(row =>
            {
                var name = Get(row, "vg_name");
                var pvPaths = pvs
                    .Where(pv => Get(pv, "vg_name").Equals(name, StringComparison.Ordinal))
                    .Select(pv => Get(pv, "pv_name"))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                return new StorageVolumeGroup(
                    name,
                    ParseLong(Get(row, "vg_size")),
                    ParseLong(Get(row, "vg_free")),
                    ParseLong(Get(row, "vg_extent_size")),
                    pvPaths);
            })
            .OrderBy(group => group.Name, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<StorageNewDisk> BuildNewDisks(
        IReadOnlyList<StorageDisk> disks,
        IReadOnlyList<StorageFileSystem> fileSystems,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> physicalVolumes,
        IReadOnlyList<StorageVolumeGroup> volumeGroups)
    {
        const long minimumBytes = 1024L * 1024L * 1024L;
        var result = new List<StorageNewDisk>();
        foreach (var disk in disks)
        {
            if (disk.SizeBytes < minimumBytes)
            {
                continue;
            }

            if (disk.Name.StartsWith("loop", StringComparison.OrdinalIgnoreCase) ||
                disk.Name.StartsWith("ram", StringComparison.OrdinalIgnoreCase) ||
                disk.Name.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ||
                disk.Transport.Equals("usb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasMountedFileSystem = fileSystems.Any(fileSystem =>
                fileSystem.DiskDevicePath == disk.DevicePath);
            if (hasMountedFileSystem)
            {
                continue;
            }

            var diskPv = physicalVolumes.Values.Any(pv =>
                NormalizeDeviceKey(Get(pv, "pv_name")).StartsWith(NormalizeDeviceKey(disk.DevicePath), StringComparison.Ordinal));
            if (diskPv || disk.Nodes.Any(node => node.Kind == StorageNodeKind.LvmPhysicalVolume))
            {
                continue;
            }

            var hasFilesystemSignature = disk.Nodes.Any(node =>
                !string.IsNullOrWhiteSpace(node.FileSystemType) ||
                !string.IsNullOrWhiteSpace(node.MountPoint));
            if (hasFilesystemSignature)
            {
                continue;
            }

            var hasPartitions = disk.Nodes.Any(node => node.Kind == StorageNodeKind.Partition);
            var actions = new List<StorageNewDiskAction>
            {
                new(
                    "mount",
                    "Use for a folder",
                    "Format the disk and mount it somewhere useful such as /mnt/data, logs or app storage.",
                    RequiresEmptyMountPoint: true,
                    RequiresVolumeGroup: false)
            };
            if (volumeGroups.Count > 0)
            {
                actions.Add(new StorageNewDiskAction(
                    "lvm",
                    "Add to existing storage pool",
                    "Add this disk into an LVM volume group so existing filesystems can grow into it.",
                    RequiresEmptyMountPoint: false,
                    RequiresVolumeGroup: true));
            }

            result.Add(new StorageNewDisk(
                disk.Id,
                disk.DevicePath,
                disk.Name,
                disk.Model,
                disk.Transport,
                disk.SizeBytes,
                disk.IsRemovable,
                hasPartitions,
                hasPartitions
                    ? "This disk has partitions but nothing is mounted. LMS can reuse it safely after confirmation."
                    : "This looks like unused capacity Linux is not using yet.",
                actions));
        }

        return result
            .OrderByDescending(disk => disk.SizeBytes)
            .ThenBy(disk => disk.DevicePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static StorageFileSystem BuildFileSystem(
        RawBlockDevice device,
        RawBlockDevice disk,
        StorageDisk storageDisk,
        IReadOnlyDictionary<string, RawMount> mounts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> pvs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> vgs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> lvs,
        IReadOnlyDictionary<string, long> fileSystemSizes)
    {
        var deviceKey = NormalizeDeviceKey(device.Path);
        mounts.TryGetValue(deviceKey, out var mount);
        lvs.TryGetValue(deviceKey, out var lv);
        var fsType = (mount?.FileSystemType ?? device.FileSystemType).ToLowerInvariant();
        var mountPoint = mount?.Target ?? device.MountPoints.First(point => !string.IsNullOrWhiteSpace(point));
        var fsSize = fileSystemSizes.TryGetValue(deviceKey, out var inspectedSize) && inspectedSize > 0
            ? inspectedSize
            : device.SizeBytes;
        var used = mount?.UsedBytes ?? device.FileSystemUsedBytes ?? 0;
        var available = mount?.AvailableBytes ?? device.FileSystemAvailableBytes ?? Math.Max(0, fsSize - used);
        var chain = Ancestors(device).Reverse().ToArray();
        var partition = chain.LastOrDefault(item => item.Type.Equals("part", StringComparison.OrdinalIgnoreCase));
        var unsupportedLayer = chain.FirstOrDefault(item =>
            !item.Type.Equals("disk", StringComparison.OrdinalIgnoreCase) &&
            !item.Type.Equals("part", StringComparison.OrdinalIgnoreCase) &&
            !item.Type.Equals("lvm", StringComparison.OrdinalIgnoreCase));
        var isLvm = device.Type.Equals("lvm", StringComparison.OrdinalIgnoreCase) && lv is not null;
        var isPlainPartition = device.Type.Equals("part", StringComparison.OrdinalIgnoreCase);
        var fullChainKnown = unsupportedLayer is null && partition is not null && (isPlainPartition || isLvm);
        var vgName = lv is null ? string.Empty : Get(lv, "vg_name");
        vgs.TryGetValue(vgName, out var vg);
        var vgFree = vg is null ? 0 : ParseLong(Get(vg, "vg_free"));
        var vgExtentSize = vg is null ? 0 : ParseLong(Get(vg, "vg_extent_size"));
        var lvSize = lv is null ? device.SizeBytes : ParseLong(Get(lv, "lv_size"));
        if (lvSize <= 0)
        {
            lvSize = device.SizeBytes;
        }

        var partitionKey = partition is null ? string.Empty : NormalizeDeviceKey(partition.Path);
        pvs.TryGetValue(partitionKey, out var pv);
        var pvPath = pv is null ? null : Get(pv, "pv_name");
        var partitionIsLast = partition is not null && IsLastPartitionOnDisk(partition, disk);
        var trailingDiskBytes = partitionIsLast ? storageDisk.UnallocatedBytes : 0;
        var containerHeadroom = Math.Max(0, (isLvm ? lvSize : partition?.SizeBytes ?? device.SizeBytes) - fsSize);
        var maximum = checked(fsSize + containerHeadroom + vgFree + (isLvm && pv is null ? 0 : trailingDiskBytes));
        var typeSupportsGrow = fsType is "ext4" or "xfs";
        var canGrow = fullChainKnown && typeSupportsGrow && maximum > fsSize + MiB && !device.ReadOnly;
        var canShrink = fullChainKnown && isLvm && fsType == "ext4" && mountPoint != "/" && !device.ReadOnly;
        var margin = Math.Max(2 * GiB, (long)Math.Ceiling(used * 0.15));
        var minimumSafe = Math.Min(fsSize, AlignUp(checked(used + margin), 4 * MiB));
        var unavailable = ResolveUnavailableReason(
            fullChainKnown,
            fsType,
            mountPoint,
            device.ReadOnly,
            canGrow,
            canShrink,
            maximum,
            fsSize,
            isPlainPartition);

        var topologyChain = chain
            .Where(item => !item.Type.Equals("disk", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Type.Equals("part", StringComparison.OrdinalIgnoreCase)
                ? item.Path
                : item.Type.Equals("lvm", StringComparison.OrdinalIgnoreCase)
                    ? $"LVM {item.Path}"
                    : $"{item.Type} {item.Path}")
            .Append($"{fsType} → {mountPoint}")
            .ToArray();

        return new StorageFileSystem(
            $"fs:{deviceKey}:{mountPoint}",
            device.Path,
            mountPoint,
            fsType,
            fsSize,
            used,
            available,
            minimumSafe,
            Math.Max(fsSize, maximum),
            canGrow,
            canShrink,
            unavailable,
            disk.Path,
            partition?.Path,
            partition?.PartitionNumber,
            pvPath,
            string.IsNullOrWhiteSpace(vgName) ? null : vgName,
            lv is null ? null : Get(lv, "lv_path"),
            isLvm ? lvSize : partition?.SizeBytes ?? device.SizeBytes,
            vgFree,
            trailingDiskBytes,
            vgExtentSize,
            Math.Max(0, maximum - fsSize),
            canShrink,
            mountPoint == "/",
            device.ReadOnly,
            mount?.Options ?? string.Empty,
            topologyChain);
    }

    private static string? ResolveUnavailableReason(
        bool fullChainKnown,
        string fsType,
        string mountPoint,
        bool readOnly,
        bool canGrow,
        bool canShrink,
        long maximum,
        long current,
        bool isPlainPartition)
    {
        if (readOnly) return "The block device is read-only.";
        if (!fullChainKnown) return "LMS does not fully understand every layer in this storage chain.";
        if (fsType is not ("ext4" or "xfs")) return $"{fsType.ToUpperInvariant()} is inspect-only in this release.";
        if (canGrow || canShrink) return null;
        if (fsType == "xfs")
        {
            return maximum > current + MiB
                ? "XFS can grow, but cannot be shrunk. Free space inside the filesystem stays on this volume."
                : "XFS filesystems cannot currently be shrunk, and no containing free space is available to grow.";
        }

        if (mountPoint == "/")
        {
            return "Offline root shrinking is coming next. Free space inside / cannot be returned to the disk while Linux is running on it.";
        }

        if (isPlainPartition)
        {
            return "Shrinking plain partitions is not enabled yet. Free space inside the filesystem stays on this partition for now.";
        }

        if (maximum <= current + MiB) return "No containing free space is currently available to grow.";
        return "No safe resize operation is currently available.";
    }

    internal static IReadOnlyList<RawBlockDevice> ParseLsblk(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("blockdevices", out var root) || root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return root.EnumerateArray().Select(element => ParseBlockDevice(element, null)).ToArray();
    }

    internal static IReadOnlyList<RawMount> ParseFindmnt(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("filesystems", out var root) || root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<RawMount>();
        foreach (var element in root.EnumerateArray())
        {
            ParseMount(element, result);
        }

        return result;
    }

    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseLvmRows(string json, string reportName)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("report", out var reports) || reports.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<IReadOnlyDictionary<string, string>>();
        foreach (var report in reports.EnumerateArray())
        {
            if (!report.TryGetProperty(reportName, out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in rows.EnumerateArray())
            {
                result.Add(row.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => JsonValue(property.Value),
                    StringComparer.OrdinalIgnoreCase));
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, long>> InspectFileSystemSizesAsync(
        IReadOnlyList<RawBlockDevice> roots,
        IReadOnlyList<RawMount> mounts,
        CancellationToken cancellationToken)
    {
        var mountLookup = mounts
            .Where(mount => mount.Source.StartsWith("/dev/", StringComparison.Ordinal))
            .GroupBy(mount => NormalizeDeviceKey(mount.Source), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var device in Flatten(roots))
        {
            var deviceKey = NormalizeDeviceKey(device.Path);
            if (!mountLookup.TryGetValue(deviceKey, out var mount))
            {
                continue;
            }

            var fileSystemType = (mount.FileSystemType ?? device.FileSystemType).ToLowerInvariant();
            LinuxCommandResult inspection;
            if (fileSystemType == "ext4")
            {
                inspection = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "tune2fs",
                        ["-l", device.Path],
                        RequiresSudo: true,
                        DiscoveryTimeout,
                        $"Read EXT4 size for {mount.Target}"),
                    dryRun: false,
                    cancellationToken);
                var size = ParseExt4Size(inspection.StandardOutput);
                if (inspection.ExitCode == 0 && size > 0)
                {
                    result[deviceKey] = size;
                }
            }
            else if (fileSystemType == "xfs")
            {
                inspection = await commandRunner.RunAsync(
                    new LinuxCommandRequest(
                        "xfs_info",
                        [mount.Target],
                        RequiresSudo: false,
                        DiscoveryTimeout,
                        $"Read XFS size for {mount.Target}")
                    {
                        IsOptionalExternalTool = true
                    },
                    dryRun: false,
                    cancellationToken);
                var size = ParseXfsSize(inspection.StandardOutput);
                if (inspection.ExitCode == 0 && size > 0)
                {
                    result[deviceKey] = size;
                }
            }
        }

        return result;
    }

    internal static long ParseExt4Size(string output)
    {
        long blocks = 0;
        long blockSize = 0;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("Block count", StringComparison.OrdinalIgnoreCase)) blocks = ParseLong(parts[1]);
            if (parts[0].Equals("Block size", StringComparison.OrdinalIgnoreCase)) blockSize = ParseLong(parts[1]);
        }

        return blocks > 0 && blockSize > 0 ? checked(blocks * blockSize) : 0;
    }

    internal static long ParseXfsSize(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output,
            @"data\s*=.*?bsize\s*=\s*(\d+).*?blocks\s*=\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, out var blockSize) &&
               long.TryParse(match.Groups[2].Value, out var blocks) && blockSize > 0 && blocks > 0
            ? checked(blockSize * blocks)
            : 0;
    }

    private async Task<StorageDisk> ReadDiskHealthAsync(StorageDisk disk, CancellationToken cancellationToken)
    {
        if (!IsSafeDevicePath(disk.DevicePath))
        {
            return disk with { Health = "Not checked" };
        }

        var result = await commandRunner.RunAsync(
            new LinuxCommandRequest(
                "smartctl",
                ["--json=c", "--health", "--attributes", disk.DevicePath],
                RequiresSudo: true,
                DiscoveryTimeout,
                $"Read health for {disk.DevicePath}")
            {
                IsOptionalExternalTool = true
            },
            dryRun: false,
            cancellationToken);
        if (result.ExitCode == 127 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return disk with { Health = "Not checked" };
        }

        var health = ParseSmartHealth(result.StandardOutput);
        return disk with
        {
            Health = health.Health,
            TemperatureCelsius = health.TemperatureCelsius,
            WearPercent = health.WearPercent
        };
    }

    internal static (string Health, int? TemperatureCelsius, int? WearPercent) ParseSmartHealth(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            bool? passed = null;
            if (root.TryGetProperty("smart_status", out var smart) &&
                smart.TryGetProperty("passed", out var passedValue) &&
                passedValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                passed = passedValue.GetBoolean();
            }

            int? temperature = null;
            if (root.TryGetProperty("temperature", out var temperatureObject) &&
                temperatureObject.TryGetProperty("current", out var currentTemperature) &&
                currentTemperature.TryGetInt32(out var current))
            {
                temperature = current;
            }

            int? wear = null;
            var criticalWarning = 0;
            if (root.TryGetProperty("nvme_smart_health_information_log", out var nvme))
            {
                if (nvme.TryGetProperty("temperature", out var nvmeTemperature) && nvmeTemperature.TryGetInt32(out var nvmeCurrent))
                {
                    temperature = nvmeCurrent > 200 ? nvmeCurrent - 273 : nvmeCurrent;
                }

                if (nvme.TryGetProperty("percentage_used", out var percentageUsed) && percentageUsed.TryGetInt32(out var used))
                {
                    wear = Math.Clamp(used, 0, 100);
                }

                if (nvme.TryGetProperty("critical_warning", out var warning) && warning.TryGetInt32(out var warningValue))
                {
                    criticalWarning = warningValue;
                }
            }

            var health = passed == false || criticalWarning != 0
                ? "Failing"
                : passed == true || root.TryGetProperty("nvme_smart_health_information_log", out _)
                    ? "Good"
                    : "Unknown";
            return (health, temperature, wear);
        }
        catch (JsonException)
        {
            return ("Unknown", null, null);
        }
    }

    private static RawBlockDevice ParseBlockDevice(JsonElement element, RawBlockDevice? parent)
    {
        var device = new RawBlockDevice(
            String(element, "name"),
            String(element, "path"),
            String(element, "type"),
            Long(element, "size"),
            String(element, "fstype"),
            Strings(element, "mountpoints"),
            NullableLong(element, "fsused"),
            NullableLong(element, "fsavail"),
            Bool(element, "ro"),
            Bool(element, "rm"),
            String(element, "model"),
            String(element, "tran"),
            Long(element, "log-sec"),
            Long(element, "phy-sec"),
            NullableLong(element, "start"),
            NullableInt(element, "partn"),
            parent);
        if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            device.Children.AddRange(children.EnumerateArray().Select(child => ParseBlockDevice(child, device)));
        }

        return device;
    }

    private static void ParseMount(JsonElement element, ICollection<RawMount> result)
    {
        result.Add(new RawMount(
            String(element, "source"),
            String(element, "target"),
            String(element, "fstype"),
            Long(element, "size"),
            Long(element, "used"),
            Long(element, "avail"),
            String(element, "options")));
        if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                ParseMount(child, result);
            }
        }
    }

    private static IEnumerable<RawBlockDevice> Flatten(IEnumerable<RawBlockDevice> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<RawBlockDevice> Ancestors(RawBlockDevice device)
    {
        for (var current = device; current is not null; current = current.Parent)
        {
            yield return current;
        }
    }

    private static bool HasSupportedMountedFileSystem(
        RawBlockDevice device,
        IReadOnlyDictionary<string, RawMount> mounts)
    {
        if (!string.IsNullOrWhiteSpace(device.Path) && mounts.ContainsKey(NormalizeDeviceKey(device.Path)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(device.FileSystemType) &&
               device.MountPoints.Any(point => !string.IsNullOrWhiteSpace(point));
    }

    private static bool IsLastPartitionOnDisk(RawBlockDevice partition, RawBlockDevice disk)
    {
        var partitions = Flatten([disk])
            .Where(item => item.Type.Equals("part", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (partitions.Length == 0)
        {
            return false;
        }

        var sectorSize = disk.LogicalSectorSize > 0 ? disk.LogicalSectorSize : 512;
        return ReferenceEquals(partition, partitions.MaxBy(item => checked((item.StartSector ?? 0) * sectorSize + item.SizeBytes)));
    }

    private static string CreateFingerprint(
        IReadOnlyList<StorageDisk> disks,
        IReadOnlyList<StorageFileSystem> fileSystems)
    {
        var canonical = new StringBuilder();
        foreach (var disk in disks.OrderBy(item => item.DevicePath, StringComparer.Ordinal))
        {
            canonical.Append(disk.DevicePath).Append('|').Append(disk.SizeBytes).Append('|')
                .Append(disk.LogicalSectorSize).Append(';');
            foreach (var node in disk.Nodes.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                canonical.Append(node.Id).Append('|').Append(node.ParentId).Append('|').Append(node.Kind).Append('|')
                    .Append(node.DevicePath).Append('|').Append(node.SizeBytes).Append('|')
                    .Append(node.FileSystemType).Append('|').Append(node.MountPoint).Append('|')
                    .Append(node.VolumeGroup).Append('|').Append(node.VolumeGroupFreeBytes).Append(';');
            }
        }

        foreach (var fileSystem in fileSystems.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            canonical.Append(fileSystem.Id).Append('|').Append(fileSystem.CurrentSizeBytes).Append('|')
                .Append(fileSystem.MaximumSizeBytes).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private Task<LinuxCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        bool optional,
        CancellationToken cancellationToken) =>
        commandRunner.RunAsync(
            new LinuxCommandRequest(fileName, arguments, RequiresSudo: false, DiscoveryTimeout, description)
            {
                IsOptionalExternalTool = optional
            },
            dryRun: false,
            cancellationToken);

    private static string BuildFailure(string message, LinuxCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail.Trim()}";
    }

    private static StorageNodeKind MapKind(string type) => type.ToLowerInvariant() switch
    {
        "disk" => StorageNodeKind.PhysicalDisk,
        "part" => StorageNodeKind.Partition,
        "raid" or "raid0" or "raid1" or "raid5" or "raid6" or "raid10" or "md" => StorageNodeKind.Raid,
        "crypt" => StorageNodeKind.EncryptedVolume,
        "lvm" => StorageNodeKind.LvmLogicalVolume,
        _ => StorageNodeKind.Unknown
    };

    private static string BuildNodeId(RawBlockDevice device) => $"block:{NormalizeDeviceKey(device.Path)}";

    private static string NormalizeDeviceKey(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("/dev/mapper/", StringComparison.Ordinal))
        {
            return normalized;
        }

        return normalized;
    }

    private static string ResolveUnavailableReasonForUnknown() =>
        "LMS could not safely map every layer between this filesystem and its disk.";

    private static bool IsSafeDevicePath(string path) =>
        path.StartsWith("/dev/", StringComparison.Ordinal) &&
        path.Skip(5).All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static long AlignUp(long value, long alignment)
    {
        if (alignment <= 0) return value;
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static long ParseLong(string? value)
    {
        var clean = (value ?? string.Empty).Trim().TrimEnd('B', 'b');
        return long.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                ? checked((long)floating)
                : 0;
    }

    private static string String(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return JsonValue(value);
    }

    private static string JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => string.Empty
    };

    private static long Long(JsonElement element, string propertyName) => ParseLong(String(element, propertyName));

    private static long? NullableLong(JsonElement element, string propertyName)
    {
        var value = String(element, propertyName);
        return string.IsNullOrWhiteSpace(value) ? null : ParseLong(value);
    }

    private static int? NullableInt(JsonElement element, string propertyName)
    {
        var value = String(element, propertyName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool Bool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return false;
        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric) && numeric != 0 ||
               value.ValueKind == JsonValueKind.String && value.GetString() is "1" or "true";
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    internal sealed class RawBlockDevice(
        string name,
        string path,
        string type,
        long sizeBytes,
        string fileSystemType,
        IReadOnlyList<string> mountPoints,
        long? fileSystemUsedBytes,
        long? fileSystemAvailableBytes,
        bool readOnly,
        bool removable,
        string model,
        string transport,
        long logicalSectorSize,
        long physicalSectorSize,
        long? startSector,
        int? partitionNumber,
        RawBlockDevice? parent)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public string Type { get; } = type;
        public long SizeBytes { get; } = sizeBytes;
        public string FileSystemType { get; } = fileSystemType;
        public IReadOnlyList<string> MountPoints { get; } = mountPoints;
        public long? FileSystemUsedBytes { get; } = fileSystemUsedBytes;
        public long? FileSystemAvailableBytes { get; } = fileSystemAvailableBytes;
        public bool ReadOnly { get; } = readOnly;
        public bool Removable { get; } = removable;
        public string Model { get; } = model;
        public string Transport { get; } = transport;
        public long LogicalSectorSize { get; } = logicalSectorSize;
        public long PhysicalSectorSize { get; } = physicalSectorSize;
        public long? StartSector { get; } = startSector;
        public int? PartitionNumber { get; } = partitionNumber;
        public RawBlockDevice? Parent { get; } = parent;
        public List<RawBlockDevice> Children { get; } = [];
    }

    internal sealed record RawMount(
        string Source,
        string Target,
        string FileSystemType,
        long SizeBytes,
        long UsedBytes,
        long AvailableBytes,
        string Options);
}
