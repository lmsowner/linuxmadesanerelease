// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiChatService
{
    Task<AiChatWorkspaceViewModel?> GetWorkspaceAsync(Guid threadId, CancellationToken cancellationToken = default);
    Task SendUserMessageAsync(Guid threadId, AiChatMessageComposer composer, CancellationToken cancellationToken = default);
    Task ReRunCommandAsync(Guid threadId, Guid invocationId, CancellationToken cancellationToken = default);
    Task RequestRollbackAsync(Guid threadId, Guid actionId, CancellationToken cancellationToken = default);
    Task AskAiToRetryCommandAsync(Guid threadId, Guid invocationId, CancellationToken cancellationToken = default);
    Task RemoveMessageAsync(Guid threadId, Guid messageId, bool truncateFromMessage = false, CancellationToken cancellationToken = default);
    Task ClearConversationAsync(Guid threadId, CancellationToken cancellationToken = default);
    Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default);
}
