// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Storage;

namespace LinuxMadeSane.Application.Interfaces;

public interface IStorageOperationRepository
{
    Task SaveAsync(StorageResizeOperation operation, CancellationToken cancellationToken = default);

    Task<StorageResizeOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StorageResizeOperation>> ListAsync(int limit = 100, CancellationToken cancellationToken = default);

    Task<bool> HasActiveOperationForDiskAsync(string diskDevicePath, CancellationToken cancellationToken = default);
}

