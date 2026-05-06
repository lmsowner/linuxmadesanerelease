using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiChatOrchestrationService
{
    Task QueueUserTurnAsync(
        AiChatThread thread,
        AiChatMessage userMessage,
        CancellationToken cancellationToken = default);

    Task ProcessRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default);
}
