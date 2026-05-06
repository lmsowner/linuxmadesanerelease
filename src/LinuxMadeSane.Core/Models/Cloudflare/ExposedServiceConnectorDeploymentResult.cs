namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceConnectorDeploymentResult(
    bool Succeeded,
    string CommandText,
    int? ExitCode,
    string Summary,
    string StandardOutput,
    string StandardError);
