// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Storage;

namespace LinuxMadeSane.Application.Interfaces;

public interface IStorageResizePlanner
{
    Task<StorageResizePlan> CreatePlanAsync(
        StorageResizePlanRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StorageValidationResult>> ValidatePlanAsync(
        StorageResizePlan plan,
        CancellationToken cancellationToken = default);
}

