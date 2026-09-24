// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiChatThreadEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public int ProviderType { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string ProviderConversationReference { get; set; } = string.Empty;
    public string ProviderStateReference { get; set; } = string.Empty;
    public int TrustLevel { get; set; }
    public bool AllowReadOnlyTools { get; set; }
    public bool AllowMutatingTools { get; set; }
    public bool RequireApprovalForMediumRisk { get; set; }
    public bool RequireApprovalForHighRisk { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<AiChatMessageEntity> Messages { get; set; } = [];
    public List<AiAttachedServerEntity> AttachedServers { get; set; } = [];
    public List<AiExecutionPlanEntity> ExecutionPlans { get; set; } = [];
    public List<AiApprovalRequestEntity> ApprovalRequests { get; set; } = [];
    public List<AiToolInvocationEntity> ToolInvocations { get; set; } = [];
    public List<AiChatRunEntity> ChatRuns { get; set; } = [];
    public List<AiAuditEntryEntity> AuditEntries { get; set; } = [];
    public List<AiChatCheckpointEntity> Checkpoints { get; set; } = [];
}
