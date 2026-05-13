using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Core.Abstractions;

public interface IEdgeGatewayCaddyManager
{
    Task<EdgeGatewayCaddyApplyResult> ApplyAsync(string caddyfile, CancellationToken cancellationToken = default);
    Task<EdgeGatewayCaddyApplyResult> RollbackAsync(CancellationToken cancellationToken = default);
    Task<EdgeGatewayCaddyApplyResult> ValidateAsync(string caddyfile, CancellationToken cancellationToken = default);
}
