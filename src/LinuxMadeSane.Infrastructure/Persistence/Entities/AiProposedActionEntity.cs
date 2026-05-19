// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiProposedActionEntity
{
    public Guid Id { get; set; }
    public Guid ExecutionPlanId { get; set; }
    public int SequenceNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ProviderToolCallId { get; set; } = string.Empty;
    public string ToolArgumentsJson { get; set; } = string.Empty;
    public string CommandPreview { get; set; } = string.Empty;
    public int RiskLevel { get; set; }
    public int ApprovalRequirement { get; set; }
    public int RequiredTrustLevel { get; set; }
    public string PolicyReason { get; set; } = string.Empty;
    public int Outcome { get; set; }
    public string SafeChangeJson { get; set; } = string.Empty;

    public AiExecutionPlanEntity? ExecutionPlan { get; set; }
}
