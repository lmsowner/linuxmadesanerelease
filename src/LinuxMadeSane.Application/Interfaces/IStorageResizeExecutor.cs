// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
