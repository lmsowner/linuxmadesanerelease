using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

public sealed class NoOpAiSafeChangeService : IAiSafeChangeService
{
    public Task<AiSafeChangeState?> AnalyzeAsync(
        Guid threadId,
        AiProposedActionProposal proposal,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AiSafeChangeState?>(proposal.SafeChange);

    public async Task<AiSafeChangeExecutionResult> ExecuteAsync(
        AiChatThread thread,
        AiProposedAction action,
        AiToolInvocation invocation,
        Func<CancellationToken, Task<AiToolExecutionResult>> executeToolAsync,
        CancellationToken cancellationToken = default) =>
        new(action, await executeToolAsync(cancellationToken));

    public Task<AiProposedActionProposal> CreateRollbackProposalAsync(
        Guid threadId,
        Guid originalActionId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Safe rollback is not available in the no-op safe-change service.");

    public Task<AiToolExecutionResult> ExecuteRollbackAsync(
        AiChatThread thread,
        AiToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Safe rollback is not available in the no-op safe-change service.");
}
