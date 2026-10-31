// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Caddy;

namespace LinuxMadeSane.Application.Interfaces;

public interface ICaddyIntegrationService
{
    Task<CaddyIntegrationDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<CaddyProxyRouteEditor> GetEditorAsync(Guid? routeId, CancellationToken cancellationToken = default);

    Task<Guid> SaveRouteAsync(CaddyProxyRouteEditor editor, CancellationToken cancellationToken = default);

    Task DeleteRouteAsync(Guid routeId, CancellationToken cancellationToken = default);

    Task<CaddyOperationResultViewModel> CheckRouteAsync(Guid routeId, CancellationToken cancellationToken = default);

    Task<CaddyOperationResultViewModel> InstallAsync(CancellationToken cancellationToken = default);

    Task<CaddyOperationResultViewModel> ReloadAsync(CancellationToken cancellationToken = default);

    Task<CaddyOperationResultViewModel> RestartAsync(CancellationToken cancellationToken = default);
}
