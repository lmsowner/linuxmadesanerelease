namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record LocalHttpServiceEndpoint(
    string Url,
    string Scheme,
    string Host,
    int Port,
    int StatusCode,
    string? Title,
    string? ServerHeader,
    string Scope = "Localhost",
    string? IpAddress = null,
    string? DisplayName = null,
    DateTimeOffset? DiscoveredAtUtc = null);
