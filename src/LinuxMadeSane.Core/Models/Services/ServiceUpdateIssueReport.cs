namespace LinuxMadeSane.Core.Models.Services;

public sealed record ServiceUpdateIssueReport(
    Guid ServiceId,
    string UnitName,
    IReadOnlyList<ServiceRepairIssue> Issues,
    IReadOnlyList<string> OneClickFixCandidates);
