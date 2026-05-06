namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareAccount(
    string Id,
    string Name,
    string Type,
    string? Status);
