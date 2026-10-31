// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Storage;

namespace LinuxMadeSane.Application.Interfaces;

public interface IStorageDiscoveryService
{
    Task<IReadOnlyList<LinuxMadeSane.Application.Contracts.Infrastructure.SmartHealth>> ReadSmartAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LinuxMadeSane.Application.Contracts.Infrastructure.SmartHealth>>([]);

    Task<StorageTopologySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<StorageTopologySnapshot> RescanAsync(CancellationToken cancellationToken = default);
}

