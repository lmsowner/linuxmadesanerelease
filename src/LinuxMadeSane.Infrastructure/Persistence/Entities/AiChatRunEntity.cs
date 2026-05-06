namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiChatRunEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid MessageId { get; set; }
    public int Status { get; set; }
    public int Step { get; set; }
    public string StatusSummary { get; set; } = string.Empty;
    public Guid? ExecutionPlanId { get; set; }
    public int ProviderAttemptCount { get; set; }
    public string CurrentProviderResponseId { get; set; } = string.Empty;
    public string PendingAssistantOutputsJson { get; set; } = string.Empty;
    public string PendingToolCallsJson { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public bool CancellationRequested { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
}
