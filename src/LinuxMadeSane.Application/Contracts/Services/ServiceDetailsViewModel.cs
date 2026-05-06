using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record ServiceDetailsViewModel(
    IReadOnlyList<LinuxServiceDefinition> AvailableServices,
    ServiceInspectionResult Inspection);
