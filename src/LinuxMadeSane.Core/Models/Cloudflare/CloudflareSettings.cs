namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareSettings(
    Guid ManagedHostId,
    string AccountId,
    string AccountName,
    string ZoneId,
    string ZoneName,
    string? ApiTokenSecretReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
