// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiApprovalDecisionEntity
{
    public Guid ApprovalRequestId { get; set; }
    public int State { get; set; }
    public int DecisionType { get; set; }
    public string DecidedBy { get; set; } = string.Empty;
    public int DecidedByTrustLevel { get; set; }
    public bool AdminOverrideUsed { get; set; }
    public bool RememberDecision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset DecidedAtUtc { get; set; }

    public AiApprovalRequestEntity? ApprovalRequest { get; set; }
}
