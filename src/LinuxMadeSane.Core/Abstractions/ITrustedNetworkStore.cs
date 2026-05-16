// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ITrustedNetworkStore
{
    Task<IReadOnlyList<TrustedNetworkEntry>> ListAsync(CancellationToken cancellationToken = default);
    Task<TrustedNetworkEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(TrustedNetworkEntry entry, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
