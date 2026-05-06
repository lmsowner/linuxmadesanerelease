using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiApprovalDecision(
    AiApprovalState State,
    AiApprovalDecisionType DecisionType,
    string DecidedBy,
    AiUserTrustLevel DecidedByTrustLevel,
    bool AdminOverrideUsed,
    bool RememberDecision,
    string Reason,
    DateTimeOffset DecidedAtUtc);
