using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICommandExecutionService
{
    Task<CommandExecutionResult> ExecuteAsync(
        ManagedHost host,
        string commandText,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
