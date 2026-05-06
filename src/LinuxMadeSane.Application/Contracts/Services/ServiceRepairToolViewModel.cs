using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record ServiceRepairToolViewModel(
    IReadOnlyList<LinuxServiceDefinition> AvailableServices,
    ServiceRepairPlan RepairPlan);
