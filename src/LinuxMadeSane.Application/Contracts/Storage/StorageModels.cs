// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Storage;

public enum StorageNodeKind
{
    Unknown = 0,
    PhysicalDisk = 1,
    Partition = 2,
    Raid = 3,
    EncryptedVolume = 4,
    LvmPhysicalVolume = 5,
    LvmVolumeGroup = 6,
    LvmLogicalVolume = 7,
    FileSystem = 8
}

public enum StorageResizeDirection
{
    Grow = 0,
    Shrink = 1,
    AttachMount = 2,
    AttachToVolumeGroup = 3
}

public enum StorageResizeRisk
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum StorageResizeStepOperation
{
    DiskRescan = 0,
    PartitionGrow = 1,
    PartitionShrink = 2,
    PvGrow = 3,
    PvShrink = 4,
    LvGrow = 5,
    LvShrink = 6,
    FileSystemGrow = 7,
    FileSystemShrink = 8,
    FileSystemCheck = 9,
    Unmount = 10,
    Mount = 11,
    Verify = 12,
    Reboot = 13,
    CreatePartitionTable = 14,
    CreatePartition = 15,
    FormatExt4 = 16,
    CreateDirectory = 17,
    UpdateFstab = 18,
    PvCreate = 19,
    VgExtend = 20,
    SettleDevices = 21
}

public enum StorageOperationState
{
    Created = 0,
    Validated = 1,
    AwaitingConfirmation = 2,
    Queued = 3,
    Running = 4,
    Verifying = 5,
    Completed = 6,
    Failed = 7,
    RecoveryRequired = 8,
    Cancelled = 9
}

public enum StorageStepState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Skipped = 4
}

public enum StorageValidationSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record StorageValidationResult(
    string Check,
    bool Passed,
    string Detail,
    StorageValidationSeverity Severity = StorageValidationSeverity.Error);

public sealed record StorageNode(
    string Id,
    string? ParentId,
    StorageNodeKind Kind,
    string Name,
    string DevicePath,
    long SizeBytes,
    string? FileSystemType = null,
    string? MountPoint = null,
    long? UsedBytes = null,
    long? AvailableBytes = null,
    int? PartitionNumber = null,
    long? StartSector = null,
    long? LogicalSectorSize = null,
    string? VolumeGroup = null,
    long? VolumeGroupFreeBytes = null,
    bool ReadOnly = false);

public sealed record StorageDisk(
    string Id,
    string DevicePath,
    string Name,
    string Model,
    string Transport,
    long SizeBytes,
    long AllocatedBytes,
    long UnallocatedBytes,
    long LogicalSectorSize,
    long PhysicalSectorSize,
    bool IsRemovable,
    string Health,
    int? TemperatureCelsius,
    int? WearPercent,
    IReadOnlyList<StorageNode> Nodes,
    bool IsNewDiskCandidate = false,
    string? NewDiskDetail = null);

public sealed record StorageVolumeGroup(
    string Name,
    long SizeBytes,
    long FreeBytes,
    long ExtentSizeBytes,
    IReadOnlyList<string> PhysicalVolumePaths);

public sealed record StorageNewDiskAction(
    string Id,
    string Title,
    string Description,
    bool RequiresEmptyMountPoint,
    bool RequiresVolumeGroup);

public sealed record StorageNewDisk(
    string Id,
    string DevicePath,
    string Name,
    string Model,
    string Transport,
    long SizeBytes,
    bool IsRemovable,
    bool HasExistingPartitions,
    string Detail,
    IReadOnlyList<StorageNewDiskAction> Actions);

public sealed record StorageFileSystem(
    string Id,
    string DevicePath,
    string MountPoint,
    string FileSystemType,
    long CurrentSizeBytes,
    long UsedBytes,
    long AvailableBytes,
    long MinimumSafeSizeBytes,
    long MaximumSizeBytes,
    bool CanGrow,
    bool CanShrink,
    string? ResizeUnavailableReason,
    string? DiskDevicePath,
    string? PartitionDevicePath,
    int? PartitionNumber,
    string? LvmPhysicalVolumePath,
    string? LvmVolumeGroup,
    string? LvmLogicalVolumePath,
    long ContainerSizeBytes,
    long VolumeGroupFreeBytes,
    long TrailingDiskFreeBytes,
    long LvmExtentSizeBytes,
    long FreeAfterGrowBytes,
    bool RequiresOfflineShrink,
    bool IsRoot,
    bool IsReadOnly,
    string MountOptions,
    IReadOnlyList<string> TopologyChain);

public sealed record StorageTopologySnapshot(
    DateTimeOffset CapturedUtc,
    string Fingerprint,
    IReadOnlyList<StorageDisk> Disks,
    IReadOnlyList<StorageFileSystem> FileSystems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<StorageNewDisk> NewDisks,
    IReadOnlyList<StorageVolumeGroup> VolumeGroups);

public sealed record StorageResizePlanRequest(
    string FileSystemId,
    long RequestedSizeBytes,
    bool UseMaximum = false);

public sealed record StorageAttachMountRequest(
    string DiskDevicePath,
    string MountPoint,
    string? Label = null);

public sealed record StorageAttachVolumeGroupRequest(
    string DiskDevicePath,
    string VolumeGroupName);

public sealed record StorageResizeStep(
    int Order,
    string Title,
    string Description,
    StorageResizeStepOperation Operation,
    string? Executable,
    IReadOnlyList<string> Arguments,
    string CommandPreview,
    bool IsMutation,
    StorageStepState State = StorageStepState.Pending,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null,
    int? ExitCode = null,
    string? StandardOutput = null,
    string? StandardError = null,
    string? VerificationDetail = null);

public sealed record StorageResizePlan(
    Guid Id,
    DateTimeOffset CreatedUtc,
    string TargetFileSystemId,
    string TargetMountPoint,
    string TargetDevicePath,
    string TargetDiskDevicePath,
    string TargetFileSystemType,
    StorageResizeDirection OperationType,
    long CurrentSizeBytes,
    long RequestedSizeBytes,
    long MinimumSizeBytes,
    long MaximumSizeBytes,
    bool RequiresUnmount,
    bool RequiresReboot,
    bool RequiresOfflineExecution,
    StorageResizeRisk RiskLevel,
    bool CanRollback,
    string TopologyFingerprint,
    string FreedSpaceDestination,
    IReadOnlyList<StorageResizeStep> Steps,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<StorageValidationResult> ValidationResults);

public sealed record StorageResizeOperation(
    Guid Id,
    StorageResizePlan Plan,
    StorageOperationState State,
    string RequestedBy,
    string Hostname,
    bool BackupAcknowledged,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureDetail,
    StorageTopologySnapshot? BeforeTopology,
    StorageTopologySnapshot? AfterTopology);

public sealed record StorageExecutionRequest(Guid PlanId, bool BackupAcknowledged, string RequestedBy);

public sealed record StorageExecutionResult(Guid OperationId, StorageOperationState State, string Detail);
