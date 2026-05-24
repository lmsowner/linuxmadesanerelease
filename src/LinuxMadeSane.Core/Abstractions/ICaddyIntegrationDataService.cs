// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Caddy;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICaddyIntegrationDataService
{
    Task<CaddyIntegrationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<CaddyProxyRouteDefinition?> GetRouteAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveRouteAsync(CaddyProxyRouteDefinition route, CancellationToken cancellationToken = default);

    Task DeleteRouteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CaddyOperationResult> CheckRouteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CaddyOperationResult> InstallAsync(CancellationToken cancellationToken = default);

    Task<CaddyOperationResult> ReloadAsync(CancellationToken cancellationToken = default);

    Task<CaddyOperationResult> RestartAsync(CancellationToken cancellationToken = default);
}
