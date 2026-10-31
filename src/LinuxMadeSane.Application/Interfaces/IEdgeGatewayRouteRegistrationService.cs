// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.EdgeGateway;

namespace LinuxMadeSane.Application.Interfaces;

public interface IEdgeGatewayRouteRegistrationService
{
    Task<EdgeGatewayClientEndpoint> RegisterClientRouteAsync(
        EdgeGatewayClientRouteRegistration registration,
        CancellationToken cancellationToken = default);

    Task UnregisterClientRouteAsync(Guid routeId, CancellationToken cancellationToken = default);
}
