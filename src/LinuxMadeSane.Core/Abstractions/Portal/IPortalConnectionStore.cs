using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Core.Abstractions.Portal;

public interface IPortalConnectionStore
{
    Task<PortalConnectionSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PortalConnectionSettings settings, CancellationToken cancellationToken = default);
}
