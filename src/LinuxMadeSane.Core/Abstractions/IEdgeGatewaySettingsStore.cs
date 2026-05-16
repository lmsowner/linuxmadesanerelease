// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Core.Abstractions;

public interface IEdgeGatewaySettingsStore
{
    Task<EdgeGatewaySettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(EdgeGatewaySettings settings, CancellationToken cancellationToken = default);
}
