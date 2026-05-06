using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalAiHardwareInspectionService
{
    Task<LocalAiHardwareProfile> InspectAsync(CancellationToken cancellationToken = default);
}
