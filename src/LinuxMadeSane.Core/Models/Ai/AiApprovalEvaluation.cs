// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiApprovalEvaluation(
    AiApprovalRequirement Requirement,
    AiUserTrustLevel RequiredTrustLevel,
    string Reason)
{
    public bool RequiresApproval =>
        Requirement is AiApprovalRequirement.UserConfirmation or AiApprovalRequirement.AdminApproval;

    public AiApprovalState RequestState =>
        Requirement == AiApprovalRequirement.Blocked
            ? AiApprovalState.Blocked
            : AiApprovalState.Pending;
}
