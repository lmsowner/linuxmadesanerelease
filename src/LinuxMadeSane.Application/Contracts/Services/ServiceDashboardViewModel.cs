using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record ServiceDashboardViewModel(
    IReadOnlyList<LinuxServiceDefinition> Services,
    IReadOnlyList<ServiceRepairIssue> HighlightedIssues);
