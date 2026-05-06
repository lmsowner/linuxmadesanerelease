namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class AiAuditEntryEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? MessageId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = string.Empty;
    public int Outcome { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public AiChatThreadEntity? Thread { get; set; }
}
