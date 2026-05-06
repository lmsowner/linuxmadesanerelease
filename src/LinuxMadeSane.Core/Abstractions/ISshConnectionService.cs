using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISshConnectionService
{
    Task<HostConnectionTestResult> TestConnectionAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default);
}
