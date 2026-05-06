using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IManagedHostHealthProbe
{
    Task<ServerHealthSnapshot> GetSnapshotAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default);
}
