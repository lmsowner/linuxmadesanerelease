namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRuntimeStatus(
    EdgeGatewayRuntimeComponentStatus Caddy,
    EdgeGatewayRuntimeComponentStatus Cloudflared,
    bool CanAttemptPublish,
    string Summary);
