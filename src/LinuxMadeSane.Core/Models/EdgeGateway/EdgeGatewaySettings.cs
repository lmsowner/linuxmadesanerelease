namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewaySettings(
    int Id,
    string GatewaySubdomain,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
