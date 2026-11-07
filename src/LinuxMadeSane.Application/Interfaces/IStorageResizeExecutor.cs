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

    Task<bool> CancelAsync(Guid operationId, CancellationToken cancellationToken = default);
}
