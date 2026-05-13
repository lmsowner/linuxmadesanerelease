using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalHttpServiceDiscoveryService
{
    Task<IReadOnlyList<LocalHttpServiceEndpoint>> DiscoverAsync(CancellationToken cancellationToken = default);
}
