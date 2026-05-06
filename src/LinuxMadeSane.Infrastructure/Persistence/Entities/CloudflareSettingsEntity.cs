namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class CloudflareSettingsEntity
{
    public Guid ManagedHostId { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;
    public string? ApiTokenSecretReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
