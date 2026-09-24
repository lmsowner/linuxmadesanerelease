// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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

