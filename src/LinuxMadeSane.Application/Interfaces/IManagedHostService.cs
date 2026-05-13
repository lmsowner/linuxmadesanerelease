using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Interfaces;

public interface IManagedHostService
{
    Task<IReadOnlyList<ManagedHost>> ListHostsAsync(CancellationToken cancellationToken = default);

    Task<ManagedHostEditor?> GetEditorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Guid> SaveHostAsync(ManagedHostEditor editor, CancellationToken cancellationToken = default);

    Task<HostConnectionTestResult> TestConnectionAsync(
        ManagedHostEditor editor,
        CancellationToken cancellationToken = default);

    Task<HostConnectionTestResult> TestConnectionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<HostConnectionTestResult> TestConnectionAsync(
        ManagedHost host,
        CancellationToken cancellationToken = default);

    Task<SshHostDiscoveryResult> DiscoverHostsAsync(
        SshHostDiscoveryScope scope,
        IProgress<SshHostDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SshHostDiscoveryResult> GetCachedHostDiscoveryAsync(
        SshHostDiscoveryScope scope,
        CancellationToken cancellationToken = default);

    Task<ManagedHostDetailsViewModel?> GetDetailsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ManagedHostLmsInstallResult> InstallLmsAsync(
        Guid id,
        ManagedHostLmsInstallOptions options,
        IProgress<ManagedHostLmsInstallProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ManagedHostLmsInstallResult> UninstallLmsAsync(
        Guid id,
        ManagedHostLmsUninstallOptions options,
        IProgress<ManagedHostLmsInstallProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ServerHealthSnapshot> GetHealthSnapshotAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ServerHealthSnapshot> GetHealthSnapshotAsync(ManagedHost host, CancellationToken cancellationToken = default);

    Task<SftpBrowserViewModel?> GetSftpBrowserAsync(Guid id, string? path, CancellationToken cancellationToken = default);
}
