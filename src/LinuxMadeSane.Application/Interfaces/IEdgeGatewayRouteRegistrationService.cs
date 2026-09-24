// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.EdgeGateway;

namespace LinuxMadeSane.Application.Interfaces;

public interface IEdgeGatewayRouteRegistrationService
{
    Task<EdgeGatewayClientEndpoint> RegisterClientRouteAsync(
        EdgeGatewayClientRouteRegistration registration,
        CancellationToken cancellationToken = default);

    Task UnregisterClientRouteAsync(Guid routeId, CancellationToken cancellationToken = default);
}
