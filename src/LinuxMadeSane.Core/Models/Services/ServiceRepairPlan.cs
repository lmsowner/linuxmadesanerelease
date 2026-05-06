namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceRepairPlan(
    Guid ServiceId,
    string UnitName,
    IReadOnlyList<ServiceRepairIssue> Issues,
    IReadOnlyList<string> SafeRepairSequence);
