// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
