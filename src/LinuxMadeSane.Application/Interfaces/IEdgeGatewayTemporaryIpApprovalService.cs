// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.EdgeGateway;

namespace LinuxMadeSane.Application.Interfaces;

public interface IEdgeGatewayTemporaryIpApprovalService
{
    Task<EdgeGatewayTemporaryIpApprovalEvaluationResult> EvaluateAsync(
        EdgeGatewayRoute route,
        EdgeGatewayTemporaryIpApprovalCheckContext context,
        CancellationToken cancellationToken = default);

    Task<EdgeGatewayTemporaryIpApprovalCompletionResult> ApproveAsync(
        string token,
        CancellationToken cancellationToken = default);
}
