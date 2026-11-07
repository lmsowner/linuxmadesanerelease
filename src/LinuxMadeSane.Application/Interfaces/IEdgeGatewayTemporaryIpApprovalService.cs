// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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

    Task<EdgeGatewayTemporaryIpApprovalCompletionResult> BlockAsync(
        string token,
        bool confirmed,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);
}
