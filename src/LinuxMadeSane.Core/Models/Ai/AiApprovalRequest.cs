// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiApprovalRequest(
    Guid Id,
    Guid ThreadId,
    Guid? ExecutionPlanId,
    Guid? ProposedActionId,
    string Title,
    string Summary,
    string ToolName,
    string CommandPreview,
    AiActionRiskLevel RiskLevel,
    AiApprovalRequirement Requirement,
    AiUserTrustLevel RequiredTrustLevel,
    AiApprovalState State,
    string PolicyReason,
    bool RememberDecisionSupported,
    DateTimeOffset RequestedAtUtc,
    AiApprovalDecision? Decision);
