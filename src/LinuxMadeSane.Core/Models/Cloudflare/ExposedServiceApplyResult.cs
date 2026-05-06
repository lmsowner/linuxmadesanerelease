namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceApplyResult(
    ExposedServiceConfig Config,
    string PublicUrl,
    string Status,
    string? NextStep,
    IReadOnlyList<ExposureWarning> Warnings,
    string? ConnectorInstallCommand,
    ExposedServiceConnectorDeploymentResult? ConnectorDeployment,
    string? ConnectorStatusCommand,
    string? ConnectorLogsCommand);
