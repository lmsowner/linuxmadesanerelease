namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServiceDryRunPlan(
    string Hostname,
    string PublicUrl,
    string TunnelName,
    bool AccessEnabled,
    bool RequiresConfirmation,
    IReadOnlyList<ExposureWarning> Warnings,
    IReadOnlyList<ExposedServicePlanStep> Steps);
