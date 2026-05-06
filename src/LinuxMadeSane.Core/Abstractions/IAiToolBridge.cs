using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiToolBridge : IAiToolRegistry
{
    Task<AiToolExecutionResult> InvokeAsync(AiToolInvocation invocation, CancellationToken cancellationToken = default);
}
