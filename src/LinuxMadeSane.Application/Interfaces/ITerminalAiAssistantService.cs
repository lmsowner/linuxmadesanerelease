using LinuxMadeSane.Application.Contracts.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface ITerminalAiAssistantService
{
    Task<TerminalAiTurnResult> ExecutePromptAsync(
        TerminalAiConversationState conversation,
        TerminalAiPromptRequest request,
        CancellationToken cancellationToken = default);
}
