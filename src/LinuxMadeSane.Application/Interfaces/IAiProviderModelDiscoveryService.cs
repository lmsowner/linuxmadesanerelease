using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiProviderModelDiscoveryService
{
    Task<IReadOnlyList<AiProviderModelOption>> DiscoverAsync(
        AiProviderSettings settings,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default);
}
