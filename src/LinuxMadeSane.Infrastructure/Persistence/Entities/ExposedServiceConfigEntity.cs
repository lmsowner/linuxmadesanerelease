namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class ExposedServiceConfigEntity
{
    public Guid Id { get; set; }
    public Guid ManagedHostId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;
    public string ZoneName { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string LocalServiceUrl { get; set; } = string.Empty;
    public string TunnelId { get; set; } = string.Empty;
    public string TunnelName { get; set; } = string.Empty;
    public string DnsRecordId { get; set; } = string.Empty;
    public string? AccessApplicationId { get; set; }
    public string? AccessPolicyId { get; set; }
    public int AccessMode { get; set; }
    public string AllowedEmailsJson { get; set; } = "[]";
    public string AllowedEmailDomainsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? DisabledAtUtc { get; set; }
}
