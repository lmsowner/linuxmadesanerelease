using LinuxMadeSane.Core.Models.Cloudflare;

namespace LinuxMadeSane.Application.Contracts.Cloudflare;

public sealed record ExposedServiceConnectorSetupResult(
    bool Succeeded,
    CloudflareTunnel Tunnel,
    string Status,
    string NextStep,
    string? ConnectorInstallCommand,
    ExposedServiceConnectorDeploymentResult? ConnectorDeployment,
    string ConnectorStatusCommand,
    string ConnectorLogsCommand);
