// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
        var requiredTrustLevel = GetRequiredTrustLevel(action.RiskLevel);

        if (action.RiskLevel == AiActionRiskLevel.ReadOnly && !trustProfile.AllowReadOnlyTools)
        {
            return new AiApprovalEvaluation(
                AiApprovalRequirement.Blocked,
                requiredTrustLevel,
                "This chat thread does not allow read-only tool execution.");
        }

        if (action.RiskLevel != AiActionRiskLevel.ReadOnly && !trustProfile.AllowMutatingTools)
        {
            return new AiApprovalEvaluation(
                AiApprovalRequirement.Blocked,
                requiredTrustLevel,
                "This chat thread does not allow mutating tool execution.");
        }

        return action.RiskLevel switch
        {
            AiActionRiskLevel.ReadOnly => userTrustLevel == AiUserTrustLevel.Untrusted
                ? new AiApprovalEvaluation(
                    AiApprovalRequirement.UserConfirmation,
                    AiUserTrustLevel.Standard,
                    "Read-only actions from an untrusted reviewer require confirmation.")
                : new AiApprovalEvaluation(
                    AiApprovalRequirement.AutoRun,
                    AiUserTrustLevel.Standard,
                    "Read-only action is allowed under the current trust profile."),
            AiActionRiskLevel.LowRiskMutation => userTrustLevel >= AiUserTrustLevel.Trusted
                ? new AiApprovalEvaluation(
                    AiApprovalRequirement.AutoRun,
                    AiUserTrustLevel.Standard,
                    "Low-risk mutation is allowed for trusted reviewers.")
                : new AiApprovalEvaluation(
                    AiApprovalRequirement.UserConfirmation,
                    AiUserTrustLevel.Standard,
                    "Low-risk mutation requires explicit confirmation."),
            AiActionRiskLevel.MediumRiskMutation => trustProfile.RequireApprovalForMediumRisk || userTrustLevel < AiUserTrustLevel.Trusted
                ? new AiApprovalEvaluation(
                    AiApprovalRequirement.UserConfirmation,
                    AiUserTrustLevel.Trusted,
                    trustProfile.RequireApprovalForMediumRisk
                        ? "Thread policy requires approval for medium-risk mutations."
                        : "Medium-risk mutation requires a trusted reviewer.")
                : new AiApprovalEvaluation(
                    AiApprovalRequirement.AutoRun,
                    AiUserTrustLevel.Trusted,
                    "Medium-risk mutation is allowed for a trusted reviewer under the current thread policy."),
            AiActionRiskLevel.HighRiskMutation => EvaluateAdminRoute(
                trustProfile.RequireApprovalForHighRisk,
                userTrustLevel,
                adminOverrideExists,
                "High-risk mutation requires an admin-level reviewer."),
            AiActionRiskLevel.Destructive => EvaluateAdminRoute(
                true,
                userTrustLevel,
                adminOverrideExists,
                "Destructive action requires admin approval and never auto-runs."),
            AiActionRiskLevel.Privileged => EvaluateAdminRoute(
                true,
                userTrustLevel,
                adminOverrideExists,
                "Privileged action requires admin approval."),
            AiActionRiskLevel.NetworkOrSecuritySensitive => EvaluateAdminRoute(
                true,
                userTrustLevel,
                adminOverrideExists,
                "Network or security sensitive action requires admin approval."),
            _ => new AiApprovalEvaluation(
                AiApprovalRequirement.Blocked,
                requiredTrustLevel,
                "The action risk could not be classified by the approval policy.")
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

        var effectiveTrustLevel = decision.AdminOverrideUsed
            ? AiUserTrustLevel.Admin
            : decision.DecidedByTrustLevel;

        if (effectiveTrustLevel < request.RequiredTrustLevel)
        {
            throw new InvalidOperationException(
                $"This approval requires {request.RequiredTrustLevel} trust. Current reviewer trust is {decision.DecidedByTrustLevel}.");
        }

        if (request.Requirement == AiApprovalRequirement.AdminApproval && effectiveTrustLevel < AiUserTrustLevel.Admin)
        {
            throw new InvalidOperationException("This approval requires an admin reviewer or admin override.");
        }
    }

    private static AiApprovalEvaluation EvaluateAdminRoute(
        bool forceApproval,
        AiUserTrustLevel userTrustLevel,
        bool adminOverrideExists,
        string reason)
    {
        if (!forceApproval && userTrustLevel == AiUserTrustLevel.Admin)
        {
            return new AiApprovalEvaluation(
                AiApprovalRequirement.AutoRun,
                AiUserTrustLevel.Admin,
                "High-risk action is allowed for an admin reviewer under the current thread policy.");
        }

        if (userTrustLevel == AiUserTrustLevel.Admin || adminOverrideExists)
        {
            return new AiApprovalEvaluation(
                AiApprovalRequirement.AdminApproval,
                AiUserTrustLevel.Admin,
                reason);
        }

        return new AiApprovalEvaluation(
            AiApprovalRequirement.Blocked,
            AiUserTrustLevel.Admin,
            $"{reason} No admin reviewer or override is currently available.");
    }

    private static AiUserTrustLevel GetRequiredTrustLevel(AiActionRiskLevel riskLevel) => riskLevel switch
    {
        AiActionRiskLevel.ReadOnly => AiUserTrustLevel.Standard,
        AiActionRiskLevel.LowRiskMutation => AiUserTrustLevel.Standard,
        AiActionRiskLevel.MediumRiskMutation => AiUserTrustLevel.Trusted,
        AiActionRiskLevel.HighRiskMutation => AiUserTrustLevel.Admin,
        AiActionRiskLevel.Destructive => AiUserTrustLevel.Admin,
        AiActionRiskLevel.Privileged => AiUserTrustLevel.Admin,
        AiActionRiskLevel.NetworkOrSecuritySensitive => AiUserTrustLevel.Admin,
        _ => AiUserTrustLevel.Admin
    };
}
