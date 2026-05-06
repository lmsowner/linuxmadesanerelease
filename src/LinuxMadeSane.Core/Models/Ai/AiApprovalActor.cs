using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiApprovalActor(
    string ActorName,
    AiUserTrustLevel TrustLevel,
    bool AdminOverrideExists);
