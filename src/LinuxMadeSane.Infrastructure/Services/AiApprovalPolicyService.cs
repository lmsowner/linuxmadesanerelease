// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class AiApprovalPolicyService : IAiApprovalPolicyService
{
    public AiApprovalEvaluation Evaluate(
        AiTrustProfile trustProfile,
        AiUserTrustLevel userTrustLevel,
        AiProposedAction action,
        bool adminOverrideExists)
    {
        return action.RiskLevel switch
        {
            AiActionRiskLevel.ReadOnly => new AiApprovalEvaluation(
                AiApprovalRequirement.AutoRun,
                AiUserTrustLevel.Standard,
                "Inspection is safe to run automatically."),
            AiActionRiskLevel.LowRiskMutation or
            AiActionRiskLevel.MediumRiskMutation or
            AiActionRiskLevel.HighRiskMutation or
            AiActionRiskLevel.Destructive or
            AiActionRiskLevel.Privileged or
            AiActionRiskLevel.NetworkOrSecuritySensitive => new AiApprovalEvaluation(
                AiApprovalRequirement.UserConfirmation,
                AiUserTrustLevel.Standard,
                "This changes the system, so Linux Made Sane will ask the user before running it with sudo."),
            _ => new AiApprovalEvaluation(
                AiApprovalRequirement.UserConfirmation,
                AiUserTrustLevel.Standard,
                "Linux Made Sane will ask the user before running this action with sudo.")
        };
    }

    public AiApprovalRequest CreateApprovalRequest(
        AiChatThread thread,
        AiProposedAction action,
        AiApprovalEvaluation evaluation,
        DateTimeOffset requestedAtUtc)
    {
        var summary = string.IsNullOrWhiteSpace(action.Description)
            ? $"Approval requested for {action.ToolName}."
            : action.Description;

        return new AiApprovalRequest(
            Guid.NewGuid(),
            thread.Id,
            action.ExecutionPlanId,
            action.Id,
            action.Title,
            summary,
            action.ToolName,
            action.CommandPreview,
            action.RiskLevel,
            evaluation.Requirement,
            evaluation.RequiredTrustLevel,
            evaluation.RequestState,
            evaluation.Reason,
            evaluation.RequiresApproval,
            requestedAtUtc,
            null);
    }

    public void EnsureDecisionAllowed(AiApprovalRequest request, AiApprovalDecision decision)
    {
        if (request.State != AiApprovalState.Pending)
        {
            throw new InvalidOperationException("Only pending approvals can be decided.");
        }

        if (decision.State == AiApprovalState.Denied)
        {
            return;
        }

    }
}
