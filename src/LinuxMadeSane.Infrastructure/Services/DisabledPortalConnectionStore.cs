using LinuxMadeSane.Core.Abstractions.Portal;
using LinuxMadeSane.Core.Models.Portal;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DisabledPortalConnectionStore : IPortalConnectionStore
{
    public Task<PortalConnectionSettings?> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PortalConnectionSettings?>(null);

    public Task SaveAsync(PortalConnectionSettings settings, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
