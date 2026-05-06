using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record SaneUpdaterViewModel(
    IReadOnlyList<LinuxServiceDefinition> AvailableServices,
    ServiceUpdatePlan UpdatePlan);
