using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProviderConnectionTester
{
    Task<AiProviderConnectionTestResult> TestAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default);
}
