// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IManagedHostStore
{
    Task<IReadOnlyList<ManagedHost>> ListAsync(CancellationToken cancellationToken = default);

    Task<ManagedHost?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(ManagedHost host, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
