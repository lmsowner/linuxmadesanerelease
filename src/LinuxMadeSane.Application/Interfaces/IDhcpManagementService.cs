// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Infrastructure;
namespace LinuxMadeSane.Application.Interfaces;
public interface IDhcpManagementService
{
    Task<DhcpWorkspace> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DhcpEditor editor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> CheckReservationAsync(DhcpReservation reservation, CancellationToken cancellationToken = default);
}
