// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Storage;

namespace LinuxMadeSane.Application.Interfaces;

public interface IStorageDiskAttachmentPlanner
{
    Task<StorageResizePlan> CreateMountPlanAsync(
        StorageAttachMountRequest request,
        CancellationToken cancellationToken = default);

    Task<StorageResizePlan> CreateVolumeGroupPlanAsync(
        StorageAttachVolumeGroupRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StorageValidationResult>> ValidatePlanAsync(
        StorageResizePlan plan,
        CancellationToken cancellationToken = default);
}
