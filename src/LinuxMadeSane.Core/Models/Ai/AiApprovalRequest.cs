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
