// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
namespace LinuxMadeSane.Application.Contracts.Storage;

public static class StorageMountRecovery
{
    public static bool CanResume(StorageResizeOperation operation) =>
        operation.State is StorageOperationState.Failed or StorageOperationState.RecoveryRequired && CanResumePlan(operation.Plan);

    public static bool CanResumePlan(StorageResizePlan plan)
    {
        var steps = plan.Steps.OrderBy(s => s.Order).ToArray();
        var index = Array.FindIndex(steps, s => s.Operation == StorageResizeStepOperation.UpdateFstab);
        return plan.OperationType == StorageResizeDirection.AttachMount && index > 0 && index == steps.Length - 2
            && steps[^1].Operation == StorageResizeStepOperation.Verify
            && steps[index].State != StorageStepState.Completed
            && steps.Take(index).All(s => s.State == StorageStepState.Completed)
            && steps.Take(index).Any(s => s.Operation == StorageResizeStepOperation.Mount);
    }
    public static StorageValidationResult CheckMountedFilesystem(StorageResizeOperation operation, StorageTopologySnapshot? current)
    {
        var plan = operation.Plan;
        StorageValidationResult Blocked(string detail) => new("Mount verification", false, detail);
        if (current is null) return Blocked("Current storage information could not be read. Refresh before retrying.");
        var mounted = current.FileSystems.FirstOrDefault(f => f.MountPoint == plan.TargetMountPoint);
        if (mounted is null) return Blocked($"{plan.TargetMountPoint} is not currently mounted. Saving it for reboot is paused.");
        if (mounted.DevicePath != plan.TargetDevicePath || !string.Equals(mounted.FileSystemType, plan.TargetFileSystemType, StringComparison.OrdinalIgnoreCase))
            return Blocked($"{plan.TargetMountPoint} is mounted from {mounted.DevicePath} as {mounted.FileSystemType}; this operation expected {plan.TargetDevicePath} as {plan.TargetFileSystemType}. Saving it for reboot is paused.");
        var previous = operation.AfterTopology?.FileSystems.FirstOrDefault(f => f.MountPoint == plan.TargetMountPoint);
        if (previous is not null)
            return new("Mount verification", mounted.Id == previous.Id, mounted.Id == previous.Id
                ? "The expected filesystem is mounted. Only saving it for reboot and verification remain."
                : "The mounted filesystem differs from the saved operation. Saving it for reboot is paused.");
        // Legacy operations may have no post-mount snapshot. Match the original disk and
        // current partition instead; never require a pre-format snapshot to contain EXT4.
        var oldDisk = operation.AfterTopology?.Disks.FirstOrDefault(d => d.DevicePath == plan.TargetDiskDevicePath)
            ?? operation.BeforeTopology?.Disks.FirstOrDefault(d => d.DevicePath == plan.TargetDiskDevicePath);
        var liveDisk = current.Disks.FirstOrDefault(d => d.DevicePath == plan.TargetDiskDevicePath);
        var oldNode = oldDisk?.Nodes.FirstOrDefault(n => n.DevicePath == plan.TargetDevicePath);
        var liveNode = liveDisk?.Nodes.FirstOrDefault(n => n.DevicePath == plan.TargetDevicePath);
        var matches = oldDisk is not null && liveDisk is not null && liveNode is not null
            && oldDisk.SizeBytes == liveDisk.SizeBytes && oldDisk.Model == liveDisk.Model && oldDisk.Transport == liveDisk.Transport
            && liveNode.FileSystemType == plan.TargetFileSystemType
            && (oldNode is null || (oldNode.Kind == liveNode.Kind && oldNode.SizeBytes == liveNode.SizeBytes
                && oldNode.StartSector == liveNode.StartSector));
        return new("Mount verification", matches, matches
            ? "The expected filesystem is mounted on the recorded disk. Only saving it for reboot and verification remain."
            : "The filesystem is mounted, but the saved operation lacks matching disk or partition details. LMS cannot safely resume this record; no changes have been made.");
    }
}
