using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolApprovalMetadata(
    AiActionRiskLevel RiskLevel,
    AiApprovalRequirement MinimumRequirement,
    bool RequiresCommandPreview,
    bool SupportsApproveOnce,
    bool SupportsRememberDecision);
