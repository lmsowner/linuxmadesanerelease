// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Infrastructure;
namespace LinuxMadeSane.Application.Interfaces;
public interface IDhcpManagementService
{
    Task<DhcpWorkspace> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DhcpEditor editor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> CheckReservationAsync(DhcpReservation reservation, CancellationToken cancellationToken = default);
}
