namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareTunnel(
    string Id,
    string AccountId,
    string Name,
    string ConfigSource,
    string Status,
    bool IsDeleted,
    bool IsManagedByLinuxMadeSane,
    DateTimeOffset CreatedAtUtc);
