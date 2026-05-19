// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Caddy;

namespace LinuxMadeSane.Application.Interfaces;

public interface ICaddyIntegrationService
{
    Task<CaddyIntegrationDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<CaddyProxyRouteEditor> GetEditorAsync(Guid? routeId, CancellationToken cancellationToken = default);

    Task<Guid> SaveRouteAsync(CaddyProxyRouteEditor editor, CancellationToken cancellationToken = default);

    Task DeleteRouteAsync(Guid routeId, CancellationToken cancellationToken = default);

    Task<CaddyOperationResultViewModel> InstallAsync(CancellationToken cancellationToken = default);

    Task<CaddyOperationResultViewModel> ReloadAsync(CancellationToken cancellationToken = default);

    Task<CaddyOperationResultViewModel> RestartAsync(CancellationToken cancellationToken = default);
}
