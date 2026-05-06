using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiSafeChangeService
{
    Task<AiSafeChangeState?> AnalyzeAsync(
        Guid threadId,
        AiProposedActionProposal proposal,
        CancellationToken cancellationToken = default);

    Task<AiSafeChangeExecutionResult> ExecuteAsync(
        AiChatThread thread,
        AiProposedAction action,
        AiToolInvocation invocation,
        Func<CancellationToken, Task<AiToolExecutionResult>> executeToolAsync,
        CancellationToken cancellationToken = default);

    Task<AiProposedActionProposal> CreateRollbackProposalAsync(
        Guid threadId,
        Guid originalActionId,
        CancellationToken cancellationToken = default);

    Task<AiToolExecutionResult> ExecuteRollbackAsync(
        AiChatThread thread,
        AiToolInvocation invocation,
        CancellationToken cancellationToken = default);
}
