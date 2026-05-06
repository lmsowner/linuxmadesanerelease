namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record CloudflareAccessPolicy(
    string Id,
    string ApplicationId,
    string Name,
    string Decision,
    IReadOnlyList<string> IncludeEmails,
    IReadOnlyList<string> IncludeEmailDomains);
