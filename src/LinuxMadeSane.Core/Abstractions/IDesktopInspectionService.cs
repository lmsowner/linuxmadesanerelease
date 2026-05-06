using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface IDesktopInspectionService
{
    Task<DesktopInspectionReport> InspectAsync(CancellationToken cancellationToken = default);
}
