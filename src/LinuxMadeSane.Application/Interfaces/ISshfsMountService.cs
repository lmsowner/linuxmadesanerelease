using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISshfsMountService
{
    Task<IReadOnlyList<SshfsMountHostCandidate>> ListHostCandidatesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedSshfsMount>> ListManagedMountsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CurrentSystemMount>> ListCurrentMountsAsync(CancellationToken cancellationToken = default);

    Task<SshfsMountResult> CreateMountAsync(SshfsMountRequest request, CancellationToken cancellationToken = default);

    Task DeleteManagedMountAsync(Guid id, CancellationToken cancellationToken = default);
}
