// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiApprovalPolicyService
{
    AiApprovalEvaluation Evaluate(
        AiTrustProfile trustProfile,
        AiUserTrustLevel userTrustLevel,
        AiProposedAction action,
        bool adminOverrideExists);

    AiApprovalRequest CreateApprovalRequest(
        AiChatThread thread,
        AiProposedAction action,
        AiApprovalEvaluation evaluation,
        DateTimeOffset requestedAtUtc);

    void EnsureDecisionAllowed(AiApprovalRequest request, AiApprovalDecision decision);
}
