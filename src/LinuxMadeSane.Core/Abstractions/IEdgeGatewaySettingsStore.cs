// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Core.Abstractions;

public interface IEdgeGatewaySettingsStore
{
    Task<EdgeGatewaySettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(EdgeGatewaySettings settings, CancellationToken cancellationToken = default);
}
