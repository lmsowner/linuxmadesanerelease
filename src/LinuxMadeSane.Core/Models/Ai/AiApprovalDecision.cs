// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
