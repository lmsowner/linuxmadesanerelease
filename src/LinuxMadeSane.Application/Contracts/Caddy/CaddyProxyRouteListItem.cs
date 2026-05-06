namespace LinuxMadeSane.Application.Contracts.Caddy;

public sealed record CaddyProxyRouteListItem(
    Guid Id,
    string Name,
    string Hostname,
    string UpstreamUrl,
    string Description,
    bool EnableTls,
    string AddressLabel,
    string GeneratedSnippet,
    DateTimeOffset UpdatedAtUtc);
