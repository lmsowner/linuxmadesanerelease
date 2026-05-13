using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalInstanceIdentityStore
{
    Task<LocalInstanceIdentity?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LocalInstanceIdentity identity, CancellationToken cancellationToken = default);
}
