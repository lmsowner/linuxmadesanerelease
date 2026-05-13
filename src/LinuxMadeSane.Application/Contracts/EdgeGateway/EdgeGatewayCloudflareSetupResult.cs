namespace LinuxMadeSane.Application.Contracts.EdgeGateway;

public sealed record EdgeGatewayCloudflareSetupResult(
    bool Success,
    bool RequiresDnsReplacement,
    bool ConnectorInstalled,
    string DomainName,
    string GatewayDomainName,
    string TunnelId,
    string TunnelName,
    string DnsTarget,
    string WildcardHostname,
    string CaddyServiceUrl,
    string Summary,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings,
    string ConnectorInstallSummary);
