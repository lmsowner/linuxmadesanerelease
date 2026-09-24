// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Storage;

namespace LinuxMadeSane.Application.Interfaces;

public interface IStorageDiscoveryService
{
    Task<StorageTopologySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<StorageTopologySnapshot> RescanAsync(CancellationToken cancellationToken = default);
}

