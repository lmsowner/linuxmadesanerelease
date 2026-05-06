namespace LinuxMadeSane.Core.Models.Cloudflare;

public sealed record ExposedServicePlanStep(
    string Action,
    string Summary,
    bool IsMutation);
