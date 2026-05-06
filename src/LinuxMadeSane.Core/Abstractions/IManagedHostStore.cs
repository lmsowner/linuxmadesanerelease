using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IManagedHostStore
{
    Task<IReadOnlyList<ManagedHost>> ListAsync(CancellationToken cancellationToken = default);

    Task<ManagedHost?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(ManagedHost host, CancellationToken cancellationToken = default);
}
