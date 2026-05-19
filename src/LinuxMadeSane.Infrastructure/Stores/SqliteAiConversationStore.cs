// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Infrastructure.Persistence;
using LinuxMadeSane.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LinuxMadeSane.Infrastructure.Stores;

public sealed class SqliteAiConversationStore(LinuxMadeSaneDbContext dbContext) : IAiConversationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<AiChatThread>> ListThreadsAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.AiChatThreads
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return items
            .OrderByDescending(thread => thread.UpdatedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task<AiChatThread?> GetThreadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatThreads
            .AsNoTracking()
            .SingleOrDefaultAsync(thread => thread.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveThreadAsync(AiChatThread thread, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatThreads
            .SingleOrDefaultAsync(existing => existing.Id == thread.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiChatThreads.Add(Map(thread));
        }
        else
        {
            entity.Title = thread.Title;
            entity.ProviderKey = thread.ProviderKey;
            entity.ProviderType = (int)thread.ProviderType;
            entity.ModelId = thread.ModelId;
            entity.ProviderConversationReference = thread.ProviderConversationReference;
            entity.ProviderStateReference = thread.ProviderStateReference;
            entity.TrustLevel = (int)thread.TrustProfile.TrustLevel;
            entity.AllowReadOnlyTools = thread.TrustProfile.AllowReadOnlyTools;
            entity.AllowMutatingTools = thread.TrustProfile.AllowMutatingTools;
            entity.RequireApprovalForMediumRisk = thread.TrustProfile.RequireApprovalForMediumRisk;
            entity.RequireApprovalForHighRisk = thread.TrustProfile.RequireApprovalForHighRisk;
            entity.CreatedAtUtc = thread.CreatedAtUtc;
            entity.UpdatedAtUtc = thread.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiChatMessage?> GetMessageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AiChatMessage>> ListMessagesAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiChatMessages.AsNoTracking();
        if (threadId.HasValue)
        {
            query = query.Where(message => message.ThreadId == threadId.Value);
        }

        var items = await query
            .OrderBy(message => message.ThreadId)
            .ThenBy(message => message.SequenceNumber)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task SaveMessageAsync(AiChatMessage message, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatMessages
            .SingleOrDefaultAsync(existing => existing.Id == message.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiChatMessages.Add(Map(message));
        }
        else
        {
            entity.ThreadId = message.ThreadId;
            entity.SequenceNumber = message.SequenceNumber;
            entity.Role = (int)message.Role;
            entity.Content = message.Content;
            entity.CreatedAtUtc = message.CreatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMessageAsync(
        Guid threadId,
        Guid messageId,
        bool truncateFromMessage,
        CancellationToken cancellationToken = default)
    {
        var targetMessage = await dbContext.AiChatMessages
            .SingleOrDefaultAsync(message => message.ThreadId == threadId && message.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("That chat message could not be found.");

        var nextUserSequenceNumber = !truncateFromMessage && (AiChatMessageRole)targetMessage.Role == AiChatMessageRole.User
            ? await dbContext.AiChatMessages
                .Where(message => message.ThreadId == threadId &&
                                  message.SequenceNumber > targetMessage.SequenceNumber &&
                                  message.Role == (int)AiChatMessageRole.User)
                .OrderBy(message => message.SequenceNumber)
                .Select(message => (int?)message.SequenceNumber)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        var messagesToRemove = await dbContext.AiChatMessages
            .Where(message => message.ThreadId == threadId &&
                              (truncateFromMessage
                                  ? message.SequenceNumber >= targetMessage.SequenceNumber
                                  : (AiChatMessageRole)targetMessage.Role == AiChatMessageRole.User
                                      ? message.SequenceNumber >= targetMessage.SequenceNumber &&
                                        (!nextUserSequenceNumber.HasValue || message.SequenceNumber < nextUserSequenceNumber.Value)
                                      : message.Id == messageId))
            .OrderBy(message => message.SequenceNumber)
            .ToListAsync(cancellationToken);

        if (messagesToRemove.Count == 0)
        {
            return;
        }

        var removedMessageIds = messagesToRemove
            .Select(message => message.Id)
            .ToHashSet();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await RemoveConversationArtifactsAsync(
            threadId,
            removedMessageIds,
            truncateFromMessage ? targetMessage.CreatedAtUtc : null,
            clearAll: false,
            cancellationToken);

        dbContext.AiChatMessages.RemoveRange(messagesToRemove);
        await dbContext.SaveChangesAsync(cancellationToken);

        await ResequenceMessagesAsync(threadId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearConversationAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        var threadExists = await dbContext.AiChatThreads
            .AnyAsync(thread => thread.Id == threadId, cancellationToken);
        if (!threadExists)
        {
            throw new InvalidOperationException("That AI chat thread could not be found.");
        }

        var messageIds = await dbContext.AiChatMessages
            .Where(message => message.ThreadId == threadId)
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await RemoveConversationArtifactsAsync(
            threadId,
            messageIds.ToHashSet(),
            null,
            clearAll: true,
            cancellationToken);

        var messages = await dbContext.AiChatMessages
            .Where(message => message.ThreadId == threadId)
            .ToListAsync(cancellationToken);

        if (messages.Count > 0)
        {
            dbContext.AiChatMessages.RemoveRange(messages);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiAttachedServer>> ListAttachedServersAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiAttachedServers.AsNoTracking();
        if (threadId.HasValue)
        {
            query = query.Where(server => server.ThreadId == threadId.Value);
        }

        var items = await query
            .OrderBy(server => server.ThreadId)
            .ThenBy(server => server.ServerName)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task ReplaceAttachedServersAsync(Guid threadId, IReadOnlyList<AiAttachedServer> attachedServers, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.AiAttachedServers
            .Where(server => server.ThreadId == threadId)
            .ToListAsync(cancellationToken);

        if (existing.Count > 0)
        {
            dbContext.AiAttachedServers.RemoveRange(existing);
        }

        if (attachedServers.Count > 0)
        {
            dbContext.AiAttachedServers.AddRange(attachedServers.Select(Map));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiExecutionPlan>> ListExecutionPlansAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiExecutionPlans
            .AsNoTracking()
            .Include(plan => plan.ProposedActions)
            .AsQueryable();

        if (threadId.HasValue)
        {
            query = query.Where(plan => plan.ThreadId == threadId.Value);
        }

        var items = await query.ToListAsync(cancellationToken);

        return items
            .OrderBy(plan => plan.ThreadId)
            .ThenBy(plan => plan.CreatedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task<AiExecutionPlan?> GetExecutionPlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiExecutionPlans
            .AsNoTracking()
            .Include(plan => plan.ProposedActions)
            .SingleOrDefaultAsync(plan => plan.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task SaveExecutionPlanAsync(AiExecutionPlan plan, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiExecutionPlans
            .Include(existing => existing.ProposedActions)
            .SingleOrDefaultAsync(existing => existing.Id == plan.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiExecutionPlans.Add(Map(plan));
        }
        else
        {
            entity.ThreadId = plan.ThreadId;
            entity.MessageId = plan.MessageId;
            entity.Summary = plan.Summary;
            entity.Outcome = (int)plan.Outcome;
            entity.CreatedAtUtc = plan.CreatedAtUtc;
            entity.UpdatedAtUtc = plan.UpdatedAtUtc;

            if (entity.ProposedActions.Count > 0)
            {
                dbContext.AiProposedActions.RemoveRange(entity.ProposedActions);
                entity.ProposedActions.Clear();
            }

            entity.ProposedActions.AddRange(plan.Actions.Select(Map));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiApprovalRequest?> GetApprovalRequestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiApprovalRequests
            .AsNoTracking()
            .Include(request => request.Decision)
            .SingleOrDefaultAsync(request => request.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AiApprovalRequest>> ListApprovalRequestsAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiApprovalRequests
            .AsNoTracking()
            .Include(request => request.Decision)
            .AsQueryable();

        if (threadId.HasValue)
        {
            query = query.Where(request => request.ThreadId == threadId.Value);
        }

        var items = await query.ToListAsync(cancellationToken);

        return items
            .OrderBy(request => request.ThreadId)
            .ThenByDescending(request => request.RequestedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task SaveApprovalRequestAsync(AiApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiApprovalRequests
            .Include(existing => existing.Decision)
            .SingleOrDefaultAsync(existing => existing.Id == request.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiApprovalRequests.Add(Map(request));
        }
        else
        {
            entity.ThreadId = request.ThreadId;
            entity.ExecutionPlanId = request.ExecutionPlanId;
            entity.ProposedActionId = request.ProposedActionId;
            entity.Title = request.Title;
            entity.Summary = request.Summary;
            entity.ToolName = request.ToolName;
            entity.CommandPreview = request.CommandPreview;
            entity.RiskLevel = (int)request.RiskLevel;
            entity.Requirement = (int)request.Requirement;
            entity.RequiredTrustLevel = (int)request.RequiredTrustLevel;
            entity.State = (int)request.State;
            entity.PolicyReason = request.PolicyReason;
            entity.RememberDecisionSupported = request.RememberDecisionSupported;
            entity.RequestedAtUtc = request.RequestedAtUtc;

            if (request.Decision is null)
            {
                if (entity.Decision is not null)
                {
                    dbContext.AiApprovalDecisions.Remove(entity.Decision);
                    entity.Decision = null;
                }
            }
            else if (entity.Decision is null)
            {
                entity.Decision = Map(request.Decision, request.Id);
            }
            else
            {
                entity.Decision.State = (int)request.Decision.State;
                entity.Decision.DecisionType = (int)request.Decision.DecisionType;
                entity.Decision.DecidedBy = request.Decision.DecidedBy;
                entity.Decision.DecidedByTrustLevel = (int)request.Decision.DecidedByTrustLevel;
                entity.Decision.AdminOverrideUsed = request.Decision.AdminOverrideUsed;
                entity.Decision.RememberDecision = request.Decision.RememberDecision;
                entity.Decision.Reason = request.Decision.Reason;
                entity.Decision.DecidedAtUtc = request.Decision.DecidedAtUtc;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiToolInvocation?> GetToolInvocationByProposedActionIdAsync(Guid proposedActionId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiToolInvocations
            .AsNoTracking()
            .Include(invocation => invocation.Result)
            .SingleOrDefaultAsync(invocation => invocation.ProposedActionId == proposedActionId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AiToolInvocation>> ListToolInvocationsAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiToolInvocations
            .AsNoTracking()
            .Include(invocation => invocation.Result)
            .AsQueryable();

        if (threadId.HasValue)
        {
            query = query.Where(invocation => invocation.ThreadId == threadId.Value);
        }

        var items = await query.ToListAsync(cancellationToken);

        return items
            .OrderBy(invocation => invocation.ThreadId)
            .ThenByDescending(invocation => invocation.StartedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task SaveToolInvocationAsync(AiToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiToolInvocations
            .SingleOrDefaultAsync(existing => existing.Id == invocation.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiToolInvocations.Add(Map(invocation));
        }
        else
        {
            entity.ThreadId = invocation.ThreadId;
            entity.MessageId = invocation.MessageId;
            entity.ExecutionPlanId = invocation.ExecutionPlanId;
            entity.ProposedActionId = invocation.ProposedActionId;
            entity.ToolName = invocation.ToolName;
            entity.ArgumentsJson = invocation.ArgumentsJson;
            entity.Status = (int)invocation.Status;
            entity.StartedAtUtc = invocation.StartedAtUtc;
            entity.CompletedAtUtc = invocation.CompletedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveToolResultAsync(AiToolResult result, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiToolResults
            .SingleOrDefaultAsync(existing => existing.Id == result.Id || existing.InvocationId == result.InvocationId, cancellationToken);

        if (entity is null)
        {
            dbContext.AiToolResults.Add(Map(result));
        }
        else
        {
            entity.InvocationId = result.InvocationId;
            entity.Outcome = (int)result.Outcome;
            entity.Summary = result.Summary;
            entity.OutputText = result.OutputText;
            entity.ErrorText = result.ErrorText;
            entity.PayloadJson = result.PayloadJson;
            entity.ExitCode = result.ExitCode;
            entity.CompletedAtUtc = result.CompletedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiAuditEntry>> ListAuditEntriesAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiAuditEntries.AsNoTracking();
        if (threadId.HasValue)
        {
            query = query.Where(entry => entry.ThreadId == threadId.Value);
        }

        var items = await query.ToListAsync(cancellationToken);

        return items
            .OrderBy(entry => entry.ThreadId)
            .ThenByDescending(entry => entry.CreatedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task SaveAuditEntryAsync(AiAuditEntry entry, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiAuditEntries
            .SingleOrDefaultAsync(existing => existing.Id == entry.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiAuditEntries.Add(Map(entry));
        }
        else
        {
            entity.ThreadId = entry.ThreadId;
            entity.MessageId = entry.MessageId;
            entity.EventType = entry.EventType;
            entity.Summary = entry.Summary;
            entity.Details = entry.Details;
            entity.Outcome = (int)entry.Outcome;
            entity.CreatedAtUtc = entry.CreatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiChatRun?> GetChatRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(run => run.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<AiChatRun?> GetChatRunByExecutionPlanIdAsync(Guid executionPlanId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatRuns
            .AsNoTracking()
            .Where(run => run.ExecutionPlanId == executionPlanId)
            .ToListAsync(cancellationToken);

        return entity
            .OrderByDescending(run => run.UpdatedAtUtc)
            .Select(Map)
            .FirstOrDefault();
    }

    public async Task<AiChatRun?> GetActiveChatRunForThreadAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatRuns
            .AsNoTracking()
            .Where(run => run.ThreadId == threadId &&
                          run.Status != (int)AiChatRunStatus.Completed &&
                          run.Status != (int)AiChatRunStatus.Failed &&
                          run.Status != (int)AiChatRunStatus.Cancelled)
            .ToListAsync(cancellationToken);

        return entity
            .OrderByDescending(run => run.UpdatedAtUtc)
            .Select(Map)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<AiChatRun>> ListChatRunsAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiChatRuns.AsNoTracking();
        if (threadId.HasValue)
        {
            query = query.Where(run => run.ThreadId == threadId.Value);
        }

        var items = await query.ToListAsync(cancellationToken);

        return items
            .OrderBy(run => run.ThreadId)
            .ThenByDescending(run => run.UpdatedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task SaveChatRunAsync(AiChatRun run, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatRuns
            .SingleOrDefaultAsync(existing => existing.Id == run.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiChatRuns.Add(Map(run));
        }
        else
        {
            entity.ThreadId = run.ThreadId;
            entity.MessageId = run.MessageId;
            entity.Status = (int)run.Status;
            entity.Step = (int)run.Step;
            entity.StatusSummary = run.StatusSummary;
            entity.ExecutionPlanId = run.ExecutionPlanId;
            entity.ProviderAttemptCount = run.ProviderAttemptCount;
            entity.CurrentProviderResponseId = run.CurrentProviderResponseId;
            entity.PendingAssistantOutputsJson = run.PendingAssistantOutputsJson;
            entity.PendingToolCallsJson = run.PendingToolCallsJson;
            entity.LastError = run.LastError;
            entity.CancellationRequested = run.CancellationRequested;
            entity.CreatedAtUtc = run.CreatedAtUtc;
            entity.UpdatedAtUtc = run.UpdatedAtUtc;
            entity.CompletedAtUtc = run.CompletedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiChatCheckpoint>> ListCheckpointsAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AiChatCheckpoints.AsNoTracking();
        if (threadId.HasValue)
        {
            query = query.Where(checkpoint => checkpoint.ThreadId == threadId.Value);
        }

        var items = await query.ToListAsync(cancellationToken);

        return items
            .OrderBy(checkpoint => checkpoint.ThreadId)
            .ThenByDescending(checkpoint => checkpoint.CreatedAtUtc)
            .Select(Map)
            .ToArray();
    }

    public async Task SaveCheckpointAsync(AiChatCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.AiChatCheckpoints
            .SingleOrDefaultAsync(existing => existing.Id == checkpoint.Id, cancellationToken);

        if (entity is null)
        {
            dbContext.AiChatCheckpoints.Add(Map(checkpoint));
        }
        else
        {
            entity.ThreadId = checkpoint.ThreadId;
            entity.MessageId = checkpoint.MessageId;
            entity.Label = checkpoint.Label;
            entity.Summary = checkpoint.Summary;
            entity.StateJson = checkpoint.StateJson;
            entity.CreatedAtUtc = checkpoint.CreatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RemoveConversationArtifactsAsync(
        Guid threadId,
        IReadOnlySet<Guid> removedMessageIds,
        DateTimeOffset? truncateFromUtc,
        bool clearAll,
        CancellationToken cancellationToken)
    {
        var executionPlans = await dbContext.AiExecutionPlans
            .Include(plan => plan.ProposedActions)
            .Where(plan => plan.ThreadId == threadId &&
                           (clearAll || (plan.MessageId.HasValue && removedMessageIds.Contains(plan.MessageId.Value))))
            .ToListAsync(cancellationToken);

        var executionPlanIds = executionPlans
            .Select(plan => plan.Id)
            .ToHashSet();
        var proposedActionIds = executionPlans
            .SelectMany(plan => plan.ProposedActions)
            .Select(action => action.Id)
            .ToHashSet();

        var approvalRequests = await dbContext.AiApprovalRequests
            .Where(request => request.ThreadId == threadId &&
                              (clearAll ||
                               (request.ExecutionPlanId.HasValue && executionPlanIds.Contains(request.ExecutionPlanId.Value)) ||
                               (request.ProposedActionId.HasValue && proposedActionIds.Contains(request.ProposedActionId.Value))))
            .ToListAsync(cancellationToken);

        if (approvalRequests.Count > 0)
        {
            dbContext.AiApprovalRequests.RemoveRange(approvalRequests);
        }

        var toolInvocations = await dbContext.AiToolInvocations
            .Where(invocation => invocation.ThreadId == threadId &&
                                 (clearAll ||
                                  (invocation.MessageId.HasValue && removedMessageIds.Contains(invocation.MessageId.Value)) ||
                                  (invocation.ExecutionPlanId.HasValue && executionPlanIds.Contains(invocation.ExecutionPlanId.Value)) ||
                                  (invocation.ProposedActionId.HasValue && proposedActionIds.Contains(invocation.ProposedActionId.Value))))
            .ToListAsync(cancellationToken);

        if (toolInvocations.Count > 0)
        {
            dbContext.AiToolInvocations.RemoveRange(toolInvocations);
        }

        var chatRuns = await dbContext.AiChatRuns
            .Where(run => run.ThreadId == threadId &&
                          (clearAll ||
                           removedMessageIds.Contains(run.MessageId) ||
                           (run.ExecutionPlanId.HasValue && executionPlanIds.Contains(run.ExecutionPlanId.Value))))
            .ToListAsync(cancellationToken);

        if (chatRuns.Count > 0)
        {
            dbContext.AiChatRuns.RemoveRange(chatRuns);
        }

        var checkpoints = await dbContext.AiChatCheckpoints
            .Where(checkpoint => checkpoint.ThreadId == threadId &&
                                 (clearAll ||
                                  (checkpoint.MessageId.HasValue && removedMessageIds.Contains(checkpoint.MessageId.Value))))
            .ToListAsync(cancellationToken);

        if (checkpoints.Count > 0)
        {
            dbContext.AiChatCheckpoints.RemoveRange(checkpoints);
        }

        var auditEntries = await dbContext.AiAuditEntries
            .Where(entry => entry.ThreadId == threadId &&
                            (clearAll ||
                             (entry.MessageId.HasValue && removedMessageIds.Contains(entry.MessageId.Value)) ||
                             (truncateFromUtc.HasValue && entry.CreatedAtUtc >= truncateFromUtc.Value)))
            .ToListAsync(cancellationToken);

        if (auditEntries.Count > 0)
        {
            dbContext.AiAuditEntries.RemoveRange(auditEntries);
        }

        if (executionPlans.Count > 0)
        {
            dbContext.AiExecutionPlans.RemoveRange(executionPlans);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ResequenceMessagesAsync(Guid threadId, CancellationToken cancellationToken)
    {
        var remainingMessages = await dbContext.AiChatMessages
            .Where(message => message.ThreadId == threadId)
            .OrderBy(message => message.SequenceNumber)
            .ToListAsync(cancellationToken);

        remainingMessages = remainingMessages
            .OrderBy(message => message.SequenceNumber)
            .ThenBy(message => message.CreatedAtUtc)
            .ToList();

        for (var index = 0; index < remainingMessages.Count; index++)
        {
            remainingMessages[index].SequenceNumber = index + 1;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AiChatThread Map(AiChatThreadEntity entity) =>
        new(
            entity.Id,
            entity.Title,
            entity.ProviderKey,
            (AiProviderType)entity.ProviderType,
            entity.ModelId,
            new AiTrustProfile(
                (AiTrustLevel)entity.TrustLevel,
                entity.AllowReadOnlyTools,
                entity.AllowMutatingTools,
                entity.RequireApprovalForMediumRisk,
                entity.RequireApprovalForHighRisk),
            entity.ProviderConversationReference,
            entity.ProviderStateReference,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);

    private static AiChatThreadEntity Map(AiChatThread thread) =>
        new()
        {
            Id = thread.Id,
            Title = thread.Title,
            ProviderKey = thread.ProviderKey,
            ProviderType = (int)thread.ProviderType,
            ModelId = thread.ModelId,
            ProviderConversationReference = thread.ProviderConversationReference,
            ProviderStateReference = thread.ProviderStateReference,
            TrustLevel = (int)thread.TrustProfile.TrustLevel,
            AllowReadOnlyTools = thread.TrustProfile.AllowReadOnlyTools,
            AllowMutatingTools = thread.TrustProfile.AllowMutatingTools,
            RequireApprovalForMediumRisk = thread.TrustProfile.RequireApprovalForMediumRisk,
            RequireApprovalForHighRisk = thread.TrustProfile.RequireApprovalForHighRisk,
            CreatedAtUtc = thread.CreatedAtUtc,
            UpdatedAtUtc = thread.UpdatedAtUtc
        };

    private static AiChatMessage Map(AiChatMessageEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.SequenceNumber,
            (AiChatMessageRole)entity.Role,
            entity.Content,
            entity.CreatedAtUtc);

    private static AiChatMessageEntity Map(AiChatMessage message) =>
        new()
        {
            Id = message.Id,
            ThreadId = message.ThreadId,
            SequenceNumber = message.SequenceNumber,
            Role = (int)message.Role,
            Content = message.Content,
            CreatedAtUtc = message.CreatedAtUtc
        };

    private static AiAttachedServer Map(AiAttachedServerEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.ManagedHostId,
            entity.ServerName,
            entity.Hostname,
            entity.Environment,
            entity.AttachedAtUtc);

    private static AiAttachedServerEntity Map(AiAttachedServer attachedServer) =>
        new()
        {
            Id = attachedServer.Id,
            ThreadId = attachedServer.ThreadId,
            ManagedHostId = attachedServer.ManagedHostId,
            ServerName = attachedServer.ServerName,
            Hostname = attachedServer.Hostname,
            Environment = attachedServer.Environment,
            AttachedAtUtc = attachedServer.AttachedAtUtc
        };

    private static AiExecutionPlan Map(AiExecutionPlanEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.MessageId,
            entity.Summary,
            (AiExecutionOutcome)entity.Outcome,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.ProposedActions
                .OrderBy(action => action.SequenceNumber)
                .Select(Map)
                .ToArray());

    private static AiExecutionPlanEntity Map(AiExecutionPlan plan) =>
        new()
        {
            Id = plan.Id,
            ThreadId = plan.ThreadId,
            MessageId = plan.MessageId,
            Summary = plan.Summary,
            Outcome = (int)plan.Outcome,
            CreatedAtUtc = plan.CreatedAtUtc,
            UpdatedAtUtc = plan.UpdatedAtUtc,
            ProposedActions = plan.Actions.Select(Map).ToList()
        };

    private static AiProposedAction Map(AiProposedActionEntity entity) =>
        new(
            entity.Id,
            entity.ExecutionPlanId,
            entity.SequenceNumber,
            entity.Title,
            entity.Description,
            entity.ToolName,
            entity.ProviderToolCallId,
            entity.ToolArgumentsJson,
            entity.CommandPreview,
            (AiActionRiskLevel)entity.RiskLevel,
            (AiApprovalRequirement)entity.ApprovalRequirement,
            (AiUserTrustLevel)entity.RequiredTrustLevel,
            entity.PolicyReason,
            (AiExecutionOutcome)entity.Outcome)
        {
            SafeChange = DeserializeSafeChange(entity.SafeChangeJson)
        };

    private static AiProposedActionEntity Map(AiProposedAction action) =>
        new()
        {
            Id = action.Id,
            ExecutionPlanId = action.ExecutionPlanId,
            SequenceNumber = action.SequenceNumber,
            Title = action.Title,
            Description = action.Description,
            ToolName = action.ToolName,
            ProviderToolCallId = action.ProviderToolCallId,
            ToolArgumentsJson = action.ToolArgumentsJson,
            CommandPreview = action.CommandPreview,
            RiskLevel = (int)action.RiskLevel,
            ApprovalRequirement = (int)action.ApprovalRequirement,
            RequiredTrustLevel = (int)action.RequiredTrustLevel,
            PolicyReason = action.PolicyReason,
            Outcome = (int)action.Outcome,
            SafeChangeJson = SerializeSafeChange(action.SafeChange)
        };

    private static AiApprovalRequest Map(AiApprovalRequestEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.ExecutionPlanId,
            entity.ProposedActionId,
            entity.Title,
            entity.Summary,
            entity.ToolName,
            entity.CommandPreview,
            (AiActionRiskLevel)entity.RiskLevel,
            (AiApprovalRequirement)entity.Requirement,
            (AiUserTrustLevel)entity.RequiredTrustLevel,
            (AiApprovalState)entity.State,
            entity.PolicyReason,
            entity.RememberDecisionSupported,
            entity.RequestedAtUtc,
            entity.Decision is null ? null : Map(entity.Decision));

    private static AiApprovalRequestEntity Map(AiApprovalRequest request) =>
        new()
        {
            Id = request.Id,
            ThreadId = request.ThreadId,
            ExecutionPlanId = request.ExecutionPlanId,
            ProposedActionId = request.ProposedActionId,
            Title = request.Title,
            Summary = request.Summary,
            ToolName = request.ToolName,
            CommandPreview = request.CommandPreview,
            RiskLevel = (int)request.RiskLevel,
            Requirement = (int)request.Requirement,
            RequiredTrustLevel = (int)request.RequiredTrustLevel,
            State = (int)request.State,
            PolicyReason = request.PolicyReason,
            RememberDecisionSupported = request.RememberDecisionSupported,
            RequestedAtUtc = request.RequestedAtUtc,
            Decision = request.Decision is null ? null : Map(request.Decision, request.Id)
        };

    private static AiApprovalDecision Map(AiApprovalDecisionEntity entity) =>
        new(
            (AiApprovalState)entity.State,
            (AiApprovalDecisionType)entity.DecisionType,
            entity.DecidedBy,
            (AiUserTrustLevel)entity.DecidedByTrustLevel,
            entity.AdminOverrideUsed,
            entity.RememberDecision,
            entity.Reason,
            entity.DecidedAtUtc);

    private static AiApprovalDecisionEntity Map(AiApprovalDecision decision, Guid requestId) =>
        new()
        {
            ApprovalRequestId = requestId,
            State = (int)decision.State,
            DecisionType = (int)decision.DecisionType,
            DecidedBy = decision.DecidedBy,
            DecidedByTrustLevel = (int)decision.DecidedByTrustLevel,
            AdminOverrideUsed = decision.AdminOverrideUsed,
            RememberDecision = decision.RememberDecision,
            Reason = decision.Reason,
            DecidedAtUtc = decision.DecidedAtUtc
        };

    private static AiToolInvocation Map(AiToolInvocationEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.MessageId,
            entity.ExecutionPlanId,
            entity.ProposedActionId,
            entity.ToolName,
            entity.ArgumentsJson,
            (AiInvocationStatus)entity.Status,
            entity.StartedAtUtc,
            entity.CompletedAtUtc,
            entity.Result is null ? null : Map(entity.Result));

    private static AiToolInvocationEntity Map(AiToolInvocation invocation) =>
        new()
        {
            Id = invocation.Id,
            ThreadId = invocation.ThreadId,
            MessageId = invocation.MessageId,
            ExecutionPlanId = invocation.ExecutionPlanId,
            ProposedActionId = invocation.ProposedActionId,
            ToolName = invocation.ToolName,
            ArgumentsJson = invocation.ArgumentsJson,
            Status = (int)invocation.Status,
            StartedAtUtc = invocation.StartedAtUtc,
            CompletedAtUtc = invocation.CompletedAtUtc
        };

    private static AiChatRun Map(AiChatRunEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.MessageId,
            (AiChatRunStatus)entity.Status,
            (AiChatRunStep)entity.Step,
            entity.StatusSummary,
            entity.ExecutionPlanId,
            entity.ProviderAttemptCount,
            entity.CurrentProviderResponseId,
            entity.PendingAssistantOutputsJson,
            entity.PendingToolCallsJson,
            entity.LastError,
            entity.CancellationRequested,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.CompletedAtUtc);

    private static AiChatRunEntity Map(AiChatRun run) =>
        new()
        {
            Id = run.Id,
            ThreadId = run.ThreadId,
            MessageId = run.MessageId,
            Status = (int)run.Status,
            Step = (int)run.Step,
            StatusSummary = run.StatusSummary,
            ExecutionPlanId = run.ExecutionPlanId,
            ProviderAttemptCount = run.ProviderAttemptCount,
            CurrentProviderResponseId = run.CurrentProviderResponseId,
            PendingAssistantOutputsJson = run.PendingAssistantOutputsJson,
            PendingToolCallsJson = run.PendingToolCallsJson,
            LastError = run.LastError,
            CancellationRequested = run.CancellationRequested,
            CreatedAtUtc = run.CreatedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc
        };

    private static AiToolResult Map(AiToolResultEntity entity) =>
        new(
            entity.Id,
            entity.InvocationId,
            (AiExecutionOutcome)entity.Outcome,
            entity.Summary,
            entity.OutputText,
            entity.ErrorText,
            entity.PayloadJson,
            entity.ExitCode,
            entity.CompletedAtUtc);

    private static AiToolResultEntity Map(AiToolResult result) =>
        new()
        {
            Id = result.Id,
            InvocationId = result.InvocationId,
            Outcome = (int)result.Outcome,
            Summary = result.Summary,
            OutputText = result.OutputText,
            ErrorText = result.ErrorText,
            PayloadJson = result.PayloadJson,
            ExitCode = result.ExitCode,
            CompletedAtUtc = result.CompletedAtUtc
        };

    private static AiAuditEntry Map(AiAuditEntryEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.MessageId,
            entity.EventType,
            entity.Summary,
            entity.Details,
            (AiExecutionOutcome)entity.Outcome,
            entity.CreatedAtUtc)
        {
            MetadataJson = entity.MetadataJson
        };

    private static AiAuditEntryEntity Map(AiAuditEntry entry) =>
        new()
        {
            Id = entry.Id,
            ThreadId = entry.ThreadId,
            MessageId = entry.MessageId,
            EventType = entry.EventType,
            Summary = entry.Summary,
            Details = entry.Details,
            MetadataJson = entry.MetadataJson,
            Outcome = (int)entry.Outcome,
            CreatedAtUtc = entry.CreatedAtUtc
        };

    private static AiSafeChangeState? DeserializeSafeChange(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiSafeChangeState>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeSafeChange(AiSafeChangeState? safeChange) =>
        safeChange is null
            ? string.Empty
            : JsonSerializer.Serialize(safeChange, SerializerOptions);

    private static AiChatCheckpoint Map(AiChatCheckpointEntity entity) =>
        new(
            entity.Id,
            entity.ThreadId,
            entity.MessageId,
            entity.Label,
            entity.Summary,
            entity.StateJson,
            entity.CreatedAtUtc);

    private static AiChatCheckpointEntity Map(AiChatCheckpoint checkpoint) =>
        new()
        {
            Id = checkpoint.Id,
            ThreadId = checkpoint.ThreadId,
            MessageId = checkpoint.MessageId,
            Label = checkpoint.Label,
            Summary = checkpoint.Summary,
            StateJson = checkpoint.StateJson,
            CreatedAtUtc = checkpoint.CreatedAtUtc
        };
}
