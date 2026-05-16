// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

public sealed class AiExecutionPlanExecutionService(
    IAiConversationStore conversationStore,
    IAiAuditService auditService,
    IAiSafeChangeService safeChangeService,
    IAiToolBridge toolBridge) : IAiExecutionPlanExecutionService
{
    public AiExecutionPlanExecutionService(
        IAiConversationStore conversationStore,
        IAiAuditService auditService,
        IAiToolBridge toolBridge)
        : this(
            conversationStore,
            auditService,
            new NoOpAiSafeChangeService(),
            toolBridge)
    {
    }

    public async Task ExecuteApprovedPlanAsync(Guid executionPlanId, CancellationToken cancellationToken = default)
    {
        var executionPlan = await conversationStore.GetExecutionPlanAsync(executionPlanId, cancellationToken)
            ?? throw new InvalidOperationException("That execution plan could not be found.");
        var thread = await conversationStore.GetThreadAsync(executionPlan.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("The AI chat thread for this execution plan could not be found.");
        var approvalRequests = (await conversationStore.ListApprovalRequestsAsync(thread.Id, cancellationToken))
            .Where(request => request.ExecutionPlanId == executionPlan.Id)
            .ToArray();

        if (approvalRequests.Any(request => request.State is AiApprovalState.Blocked or AiApprovalState.Denied))
        {
            await conversationStore.SaveExecutionPlanAsync(executionPlan with
            {
                Outcome = AiExecutionOutcome.Rejected,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Actions = executionPlan.Actions
                    .Select(action => approvalRequests.Any(request => request.ProposedActionId == action.Id &&
                                                                     request.State is AiApprovalState.Blocked or AiApprovalState.Denied)
                        ? action with { Outcome = AiExecutionOutcome.Rejected }
                        : action)
                    .ToArray()
            }, cancellationToken);
            return;
        }

        if (approvalRequests.Any(request => request.State == AiApprovalState.Pending))
        {
            return;
        }

        var updatedActions = executionPlan.Actions.ToDictionary(action => action.Id, action => action);
        var executedAnyAction = false;

        foreach (var action in executionPlan.Actions.OrderBy(action => action.SequenceNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingInvocation = await conversationStore.GetToolInvocationByProposedActionIdAsync(action.Id, cancellationToken);
            if (existingInvocation?.Result is not null)
            {
                updatedActions[action.Id] = action with { Outcome = existingInvocation.Result.Outcome };
                continue;
            }

            if (existingInvocation is not null && existingInvocation.Status == AiInvocationStatus.Running)
            {
                var staleResult = new AiToolResult(
                    CreateDeterministicGuid($"{existingInvocation.Id}:result"),
                    existingInvocation.Id,
                    AiExecutionOutcome.Failed,
                    $"{action.ToolName} did not finish before a retry was attempted, so Linux Made Sane marked it failed instead of running it twice.",
                    string.Empty,
                    "The previous invocation was still marked as running.",
                    string.Empty,
                    null,
                    DateTimeOffset.UtcNow);

                await conversationStore.SaveToolResultAsync(staleResult, cancellationToken);
                await conversationStore.SaveToolInvocationAsync(existingInvocation with
                {
                    Status = AiInvocationStatus.Failed,
                    CompletedAtUtc = staleResult.CompletedAtUtc,
                    Result = staleResult
                }, cancellationToken);

                updatedActions[action.Id] = action with { Outcome = staleResult.Outcome };
                await RecordAuditAsync(
                    thread.Id,
                    executionPlan.MessageId,
                    $"manual-tool-stale:{action.Id}",
                    "tool.invocation.failed",
                    "Tool invocation marked failed before rerun",
                    $"{action.ToolName} | {staleResult.Summary}",
                    staleResult.Outcome,
                    staleResult.CompletedAtUtc,
                    BuildSafeChangeMetadata(action),
                    cancellationToken);
                continue;
            }

            executedAnyAction = true;
            var startedAtUtc = DateTimeOffset.UtcNow;
            var invocation = new AiToolInvocation(
                CreateDeterministicGuid($"{action.Id}:manual:invocation"),
                thread.Id,
                executionPlan.MessageId,
                executionPlan.Id,
                action.Id,
                action.ToolName,
                action.ToolArgumentsJson,
                AiInvocationStatus.Running,
                startedAtUtc,
                null,
                null);

            await conversationStore.SaveToolInvocationAsync(invocation, cancellationToken);
            await RecordAuditAsync(
                thread.Id,
                executionPlan.MessageId,
                $"manual-tool-start:{action.Id}",
                "tool.invocation.started",
                "Manual tool invocation started",
                $"{action.ToolName} | {action.Title}",
                AiExecutionOutcome.Pending,
                startedAtUtc,
                BuildSafeChangeMetadata(action),
                cancellationToken);

            AiToolResult persistedResult;
            AiInvocationStatus invocationStatus;
            AiProposedAction executedAction = action;

            try
            {
                AiToolExecutionResult executionResult;
                if (action.SafeChange is not null && !action.ToolName.Equals(AiToolNames.RollbackSafeChange, StringComparison.OrdinalIgnoreCase))
                {
                    var safeChangeResult = await safeChangeService.ExecuteAsync(
                        thread,
                        action,
                        invocation,
                        toolCancellationToken => toolBridge.InvokeAsync(invocation, toolCancellationToken),
                        cancellationToken);
                    executionResult = safeChangeResult.ExecutionResult;
                    executedAction = safeChangeResult.UpdatedAction;
                }
                else
                {
                    executionResult = await toolBridge.InvokeAsync(invocation, cancellationToken);
                }

                persistedResult = executionResult.PersistedResult with
                {
                    Id = CreateDeterministicGuid($"{invocation.Id}:result")
                };
                invocationStatus = MapInvocationStatus(persistedResult.Outcome);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                persistedResult = new AiToolResult(
                    CreateDeterministicGuid($"{invocation.Id}:result"),
                    invocation.Id,
                    AiExecutionOutcome.Failed,
                    $"{action.ToolName} failed before Linux Made Sane could produce a tool result.",
                    string.Empty,
                    exception.Message,
                    string.Empty,
                    null,
                    DateTimeOffset.UtcNow);
                invocationStatus = AiInvocationStatus.Failed;
            }

            await conversationStore.SaveToolResultAsync(persistedResult, cancellationToken);
            await conversationStore.SaveToolInvocationAsync(invocation with
            {
                Status = invocationStatus,
                CompletedAtUtc = persistedResult.CompletedAtUtc,
                Result = persistedResult
            }, cancellationToken);

            updatedActions[action.Id] = executedAction with { Outcome = persistedResult.Outcome };
            await conversationStore.SaveExecutionPlanAsync(executionPlan with
            {
                UpdatedAtUtc = persistedResult.CompletedAtUtc,
                Actions = executionPlan.Actions
                    .Select(planAction => updatedActions.TryGetValue(planAction.Id, out var updatedAction)
                        ? updatedAction
                        : planAction)
                    .ToArray()
            }, cancellationToken);

            await RecordAuditAsync(
                thread.Id,
                executionPlan.MessageId,
                $"manual-tool-finish:{action.Id}",
                persistedResult.Outcome switch
                {
                    AiExecutionOutcome.Succeeded => "tool.invocation.completed",
                    AiExecutionOutcome.Cancelled => "tool.invocation.cancelled",
                    _ => "tool.invocation.failed"
                },
                persistedResult.Outcome switch
                {
                    AiExecutionOutcome.Succeeded => "Manual tool invocation completed",
                    AiExecutionOutcome.Cancelled => "Manual tool invocation cancelled",
                    _ => "Manual tool invocation failed"
                },
                $"{action.ToolName} | {persistedResult.Summary}",
                persistedResult.Outcome,
                persistedResult.CompletedAtUtc,
                BuildSafeChangeMetadata(updatedActions[action.Id]),
                cancellationToken);
        }

        var finalActions = executionPlan.Actions
            .Select(action => updatedActions[action.Id])
            .ToArray();
        var completedAtUtc = DateTimeOffset.UtcNow;
        var finalPlan = executionPlan with
        {
            Outcome = BuildPlanOutcome(finalActions),
            UpdatedAtUtc = completedAtUtc,
            Actions = finalActions
        };

        await conversationStore.SaveExecutionPlanAsync(finalPlan, cancellationToken);
        await conversationStore.SaveThreadAsync(thread with { UpdatedAtUtc = completedAtUtc }, cancellationToken);

        if (executedAnyAction)
        {
            await RecordAuditAsync(
                thread.Id,
                executionPlan.MessageId,
                $"manual-plan-finish:{executionPlan.Id}",
                finalPlan.Outcome == AiExecutionOutcome.Succeeded
                    ? "execution-plan.completed"
                    : "execution-plan.failed",
                finalPlan.Outcome == AiExecutionOutcome.Succeeded
                    ? "Manual execution plan completed"
                    : "Manual execution plan finished with failures",
                $"{finalActions.Length} action(s) processed without provider continuation.",
                finalPlan.Outcome,
                completedAtUtc,
                JsonSerializer.Serialize(finalActions.Select(action => new
                {
                    action.Id,
                    action.Title,
                    action.Outcome,
                    action.SafeChange
                })),
                cancellationToken);
        }
    }

    private async Task RecordAuditAsync(
        Guid threadId,
        Guid? messageId,
        string token,
        string eventType,
        string summary,
        string details,
        AiExecutionOutcome outcome,
        DateTimeOffset createdAtUtc,
        string metadataJson,
        CancellationToken cancellationToken)
    {
        await auditService.RecordAsync(
            new AiAuditEntry(
                CreateDeterministicGuid($"{threadId}:manual:{token}"),
                threadId,
                messageId,
                eventType,
                summary,
                details,
                outcome,
                createdAtUtc)
            {
                MetadataJson = metadataJson
            },
            cancellationToken);
    }

    private static string BuildSafeChangeMetadata(AiProposedAction action) =>
        JsonSerializer.Serialize(new
        {
            action.Id,
            action.ToolName,
            action.RiskLevel,
            action.SafeChange
        });

    private static AiInvocationStatus MapInvocationStatus(AiExecutionOutcome outcome) => outcome switch
    {
        AiExecutionOutcome.Succeeded => AiInvocationStatus.Succeeded,
        AiExecutionOutcome.Cancelled => AiInvocationStatus.Cancelled,
        _ => AiInvocationStatus.Failed
    };

    private static AiExecutionOutcome BuildPlanOutcome(IReadOnlyList<AiProposedAction> actions)
    {
        if (actions.Any(action => action.Outcome == AiExecutionOutcome.Cancelled))
        {
            return AiExecutionOutcome.Cancelled;
        }

        if (actions.Any(action => action.Outcome == AiExecutionOutcome.Rejected))
        {
            return AiExecutionOutcome.Rejected;
        }

        if (actions.Any(action => action.Outcome == AiExecutionOutcome.Failed))
        {
            return AiExecutionOutcome.Failed;
        }

        return actions.All(action => action.Outcome == AiExecutionOutcome.Succeeded)
            ? AiExecutionOutcome.Succeeded
            : AiExecutionOutcome.Pending;
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes[..16].CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
