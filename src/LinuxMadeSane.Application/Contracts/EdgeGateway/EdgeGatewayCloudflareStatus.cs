namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflareStatus(
    bool HasSavedToken,
    bool IsAvailable,
    string Message,
    IReadOnlyList<EdgeGatewayCloudflareDomainOption> Domains);
