using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Core.Abstractions;

public interface IEdgeGatewaySettingsStore
{
    Task<EdgeGatewaySettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(EdgeGatewaySettings settings, CancellationToken cancellationToken = default);
}
