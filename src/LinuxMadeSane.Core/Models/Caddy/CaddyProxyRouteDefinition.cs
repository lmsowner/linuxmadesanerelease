namespace LinuxMadeSane.Core.Models.Caddy;

public sealed record CaddyProxyRouteDefinition(
    Guid Id,
    string Name,
    string Hostname,
    string UpstreamUrl,
    string Description,
    bool EnableTls,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
