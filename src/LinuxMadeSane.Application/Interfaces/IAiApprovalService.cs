// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
