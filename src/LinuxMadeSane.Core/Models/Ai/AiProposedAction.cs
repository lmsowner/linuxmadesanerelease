// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProposedAction(
    Guid Id,
    Guid ExecutionPlanId,
    int SequenceNumber,
    string Title,
    string Description,
    string ToolName,
    string ProviderToolCallId,
    string ToolArgumentsJson,
    string CommandPreview,
    AiActionRiskLevel RiskLevel,
    AiApprovalRequirement ApprovalRequirement,
    AiUserTrustLevel RequiredTrustLevel,
    string PolicyReason,
    AiExecutionOutcome Outcome)
{
    public AiSafeChangeState? SafeChange { get; init; }

    public bool RequiresApproval =>
        ApprovalRequirement is AiApprovalRequirement.UserConfirmation or AiApprovalRequirement.AdminApproval;
}
