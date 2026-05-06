using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceConfig(
    Guid Id,
    Guid ManagedHostId,
    string ServiceName,
    string AccountId,
    string AccountName,
    string ZoneId,
    string ZoneName,
    string Hostname,
    string LocalServiceUrl,
    string TunnelId,
    string TunnelName,
    string DnsRecordId,
    string? AccessApplicationId,
    string? AccessPolicyId,
    ExposedServiceAccessMode AccessMode,
    IReadOnlyList<string> AllowedEmails,
    IReadOnlyList<string> AllowedEmailDomains,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DisabledAtUtc);
