namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareValidationResult(
    bool HasSavedToken,
    IReadOnlyList<CloudflareAccount> Accounts,
    IReadOnlyList<CloudflareZone> Zones);
