// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Security;

namespace LinuxMadeSane.Application.Interfaces;

public interface IPortForwardingService
{
    Task<PortForwardingOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task InstallSocatAsync(CancellationToken cancellationToken = default);

    Task<PortForwardPortCheck> CheckSourcePortAsync(
        PortForwardEditor editor,
        CancellationToken cancellationToken = default);

    Task SaveAsync(PortForwardEditor editor, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
