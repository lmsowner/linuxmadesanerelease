namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayRemoteLmsRelaySetupResult(
    bool Success,
    string DomainName,
    string Hostname,
    string DnsTarget,
    string TunnelName,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings);
