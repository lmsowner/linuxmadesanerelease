using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISshHostDiscoveryService
{
    Task<SshHostDiscoveryResult> GetCachedHostsAsync(
        SshHostDiscoveryScope scope,
        CancellationToken cancellationToken = default);

    Task<SshHostDiscoveryResult> DiscoverHostsAsync(
        SshHostDiscoveryScope scope,
        IProgress<SshHostDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
