// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiApprovalService
{
    Task<AiExecutionPlan> ProposeExecutionPlanAsync(
        Guid threadId,
        AiExecutionPlanProposal proposal,
        AiApprovalActor actor,
        CancellationToken cancellationToken = default);

    Task DecideApprovalAsync(
        Guid requestId,
        AiApprovalDecisionCommand command,
        CancellationToken cancellationToken = default);
}
