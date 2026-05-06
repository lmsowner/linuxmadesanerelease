using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed class AiApprovalDecisionCommand
{
    public AiApprovalDecisionType DecisionType { get; set; } = AiApprovalDecisionType.ApproveOnce;
    public string ActorName { get; set; } = "Local operator";
    public AiUserTrustLevel UserTrustLevel { get; set; } = AiUserTrustLevel.Standard;
    public bool AdminOverrideUsed { get; set; }
    public bool RememberDecision { get; set; }
    public string Reason { get; set; } = string.Empty;
}
