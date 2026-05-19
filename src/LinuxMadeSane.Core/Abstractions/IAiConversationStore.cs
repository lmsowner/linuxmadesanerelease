// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiConversationStore
{
    Task<IReadOnlyList<AiChatThread>> ListThreadsAsync(CancellationToken cancellationToken = default);
    Task<AiChatThread?> GetThreadAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveThreadAsync(AiChatThread thread, CancellationToken cancellationToken = default);

    Task<AiChatMessage?> GetMessageAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChatMessage>> ListMessagesAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task SaveMessageAsync(AiChatMessage message, CancellationToken cancellationToken = default);
    Task RemoveMessageAsync(Guid threadId, Guid messageId, bool truncateFromMessage, CancellationToken cancellationToken = default);
    Task ClearConversationAsync(Guid threadId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiAttachedServer>> ListAttachedServersAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task ReplaceAttachedServersAsync(Guid threadId, IReadOnlyList<AiAttachedServer> attachedServers, CancellationToken cancellationToken = default);

    Task<AiExecutionPlan?> GetExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiExecutionPlan>> ListExecutionPlansAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task SaveExecutionPlanAsync(AiExecutionPlan plan, CancellationToken cancellationToken = default);

    Task<AiApprovalRequest?> GetApprovalRequestAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiApprovalRequest>> ListApprovalRequestsAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task SaveApprovalRequestAsync(AiApprovalRequest request, CancellationToken cancellationToken = default);

    Task<AiToolInvocation?> GetToolInvocationByProposedActionIdAsync(Guid proposedActionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiToolInvocation>> ListToolInvocationsAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task SaveToolInvocationAsync(AiToolInvocation invocation, CancellationToken cancellationToken = default);
    Task SaveToolResultAsync(AiToolResult result, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiAuditEntry>> ListAuditEntriesAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task SaveAuditEntryAsync(AiAuditEntry entry, CancellationToken cancellationToken = default);

    Task<AiChatRun?> GetChatRunAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AiChatRun?> GetChatRunByExecutionPlanIdAsync(Guid executionPlanId, CancellationToken cancellationToken = default);
    Task<AiChatRun?> GetActiveChatRunForThreadAsync(Guid threadId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChatRun>> ListChatRunsAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task SaveChatRunAsync(AiChatRun run, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiChatCheckpoint>> ListCheckpointsAsync(Guid? threadId = null, CancellationToken cancellationToken = default);
    Task SaveCheckpointAsync(AiChatCheckpoint checkpoint, CancellationToken cancellationToken = default);
}
