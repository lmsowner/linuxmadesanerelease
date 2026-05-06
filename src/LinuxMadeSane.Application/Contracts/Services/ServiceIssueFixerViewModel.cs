using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record ServiceIssueFixerViewModel(
    IReadOnlyList<LinuxServiceDefinition> AvailableServices,
    ServiceUpdateIssueReport IssueReport);
