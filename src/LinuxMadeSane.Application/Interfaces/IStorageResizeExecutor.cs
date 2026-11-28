// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Storage;

namespace LinuxMadeSane.Application.Interfaces;

public interface IStorageResizeExecutor
{
    Task<StorageExecutionResult> QueueAsync(
        StorageResizePlan plan,
        StorageExecutionRequest request,
        CancellationToken cancellationToken = default);

    Task<StorageExecutionResult> RetryMountPersistenceAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Mount persistence recovery is unavailable.");

    Task<bool> CancelAsync(Guid operationId, CancellationToken cancellationToken = default);
}
