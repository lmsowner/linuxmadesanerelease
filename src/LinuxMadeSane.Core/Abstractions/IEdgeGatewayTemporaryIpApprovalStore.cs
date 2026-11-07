// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Core.Abstractions;

public interface IEdgeGatewayTemporaryIpApprovalStore
{
    Task<EdgeGatewayTemporaryIpApprovalConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(
        EdgeGatewayTemporaryIpApprovalConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task ResetTransientStateAsync(
        DateTimeOffset resetAtUtc,
        CancellationToken cancellationToken = default);
}
