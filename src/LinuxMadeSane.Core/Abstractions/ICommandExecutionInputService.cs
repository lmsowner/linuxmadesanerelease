using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ICommandExecutionInputService
{
    Task<CommandExecutionResult> ExecuteAsync(
        ManagedHost host,
        string commandText,
        CommandExecutionInput input,
        IProgress<CommandExecutionUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}
