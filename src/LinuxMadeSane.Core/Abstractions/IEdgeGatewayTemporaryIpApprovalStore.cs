// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Core.Abstractions;

public interface IEdgeGatewayTemporaryIpApprovalStore
{
    Task<EdgeGatewayTemporaryIpApprovalConfiguration> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(
        EdgeGatewayTemporaryIpApprovalConfiguration configuration,
        CancellationToken cancellationToken = default);
}
