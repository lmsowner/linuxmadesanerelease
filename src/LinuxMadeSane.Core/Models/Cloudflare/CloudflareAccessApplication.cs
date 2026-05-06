namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareAccessApplication(
    string Id,
    string AccountId,
    string Name,
    string Domain,
    string Type,
    string AudienceTag,
    string SessionDuration);
