namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareZone(
    string Id,
    string Name,
    string AccountId,
    string AccountName,
    string Status,
    bool Paused);
