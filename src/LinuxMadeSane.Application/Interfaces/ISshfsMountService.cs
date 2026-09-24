// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISshfsMountService
{
    Task<IReadOnlyList<SshfsMountHostCandidate>> ListHostCandidatesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedSshfsMount>> ListManagedMountsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CurrentSystemMount>> ListCurrentMountsAsync(CancellationToken cancellationToken = default);

    Task<SshfsMountResult> CreateMountAsync(SshfsMountRequest request, CancellationToken cancellationToken = default);

    Task<SshfsMountResult?> ReconnectManagedMountAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<SshfsMountResult?>(null);

    Task DeleteManagedMountAsync(Guid id, CancellationToken cancellationToken = default);
}
