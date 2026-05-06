using LinuxMadeSane.Core.Models.Services;

namespace LinuxMadeSane.Application.Contracts.Services;

public sealed record ServiceHelperViewModel(
    IReadOnlyList<LinuxServiceDefinition> AvailableServices,
    ServiceInspectionResult Inspection);
