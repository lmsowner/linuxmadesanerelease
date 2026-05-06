using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILinuxCommandRunner
{
    Task<LinuxCommandResult> RunAsync(
        LinuxCommandRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
