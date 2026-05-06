namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalAiAuditEntryEntity
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
