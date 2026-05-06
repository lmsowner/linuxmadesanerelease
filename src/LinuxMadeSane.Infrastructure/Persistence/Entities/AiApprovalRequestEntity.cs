namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiApprovalRequestEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? ExecutionPlanId { get; set; }
    public Guid? ProposedActionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string CommandPreview { get; set; } = string.Empty;
    public int RiskLevel { get; set; }
    public int Requirement { get; set; }
    public int RequiredTrustLevel { get; set; }
    public int State { get; set; }
    public string PolicyReason { get; set; } = string.Empty;
    public bool RememberDecisionSupported { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
    public AiApprovalDecisionEntity? Decision { get; set; }
}
