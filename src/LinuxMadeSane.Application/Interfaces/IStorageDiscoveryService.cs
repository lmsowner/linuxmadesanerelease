// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Storage;

namespace LinuxMadeSane.Application.Interfaces;

public interface IStorageDiscoveryService
{
    Task<IReadOnlyList<LinuxMadeSane.Application.Contracts.Infrastructure.SmartHealth>> ReadSmartAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LinuxMadeSane.Application.Contracts.Infrastructure.SmartHealth>>([]);

    Task<StorageTopologySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<StorageTopologySnapshot> RescanAsync(CancellationToken cancellationToken = default);
}

