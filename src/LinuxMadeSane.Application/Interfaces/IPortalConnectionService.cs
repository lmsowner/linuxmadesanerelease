using LinuxMadeSane.Application.Contracts.Portal;

namespace LinuxMadeSane.Application.Interfaces;

public interface IPortalConnectionService
{
    Task<PortalConnectionWorkspaceViewModel> GetWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<PortalConnectionWorkspaceViewModel> SaveAsync(
        PortalConnectionEditor editor,
        CancellationToken cancellationToken = default);

    Task<PortalConnectionWorkspaceViewModel> RotatePairingCodeAsync(CancellationToken cancellationToken = default);

    Task<PortalConnectionWorkspaceViewModel> UnpairAsync(CancellationToken cancellationToken = default);
}
