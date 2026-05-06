using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

public sealed class AiChatTurnOrchestrator(
    IAiConversationStore conversationStore,
    IAiProviderRegistry providerRegistry,
    IAiApprovalService approvalService,
    IAiAuditService auditService,
    IAiToolBridge toolBridge,
    IAiChatRunQueue runQueue) : IAiChatOrchestrationService
{
    private const int MaxProviderAttempts = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task QueueUserTurnAsync(
        AiChatThread thread,
        AiChatMessage userMessage,
        CancellationToken cancellationToken = default)
    {
        if (thread.ProviderType == AiProviderType.Unknown || string.IsNullOrWhiteSpace(thread.ProviderKey))
        {
            return;
        }

        var existingRuns = await conversationStore.ListChatRunsAsync(thread.Id, cancellationToken);
        var existingRun = existingRuns.FirstOrDefault(run => run.MessageId == userMessage.Id);
        if (existingRun is not null)
        {
            await runQueue.EnqueueAsync(existingRun.Id, cancellationToken);
            return;
        }

        var activeRun = existingRuns.FirstOrDefault(run => !run.IsTerminal);
        if (activeRun is not null)
        {
            throw new InvalidOperationException("This chat already has an active AI turn. Wait for it to complete or cancel it before sending another message.");
        }

        var now = DateTimeOffset.UtcNow;
        var run = new AiChatRun(
            CreateDeterministicGuid($"{userMessage.Id}:run"),
            thread.Id,
            userMessage.Id,
            AiChatRunStatus.Queued,
            AiChatRunStep.Queued,
            "Queued for provider execution.",
            null,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            now,
            now,
            null);

        await conversationStore.SaveChatRunAsync(run, cancellationToken);
        await RecordAuditAsync(
            run,
            "queued",
            "orchestration.queued",
            "AI orchestration queued",
            $"Queued a resumable AI turn for message {userMessage.Id}.",
            AiExecutionOutcome.Pending,
            now,
            cancellationToken);

        await runQueue.EnqueueAsync(run.Id, cancellationToken);
    }

    public async Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await conversationStore.GetChatRunAsync(runId, cancellationToken);
        if (run is null || run.IsTerminal)
        {
            return;
        }

        if (run.Status == AiChatRunStatus.AwaitingApproval)
        {
            throw new InvalidOperationException("This AI turn is waiting for approval. Resolve the approval request instead of cancelling execution.");
        }

        var now = DateTimeOffset.UtcNow;
        AiChatRun updatedRun;

        if (run.Status == AiChatRunStatus.Queued)
        {
            updatedRun = run with
            {
                Status = AiChatRunStatus.Cancelled,
                Step = AiChatRunStep.Cancelled,
                StatusSummary = "Cancelled before provider execution started.",
                CancellationRequested = true,
                UpdatedAtUtc = now,
                CompletedAtUtc = now
            };
        }
        else
        {
            updatedRun = run with
            {
                Status = AiChatRunStatus.CancellationRequested,
                StatusSummary = "Cancellation requested.",
                CancellationRequested = true,
                UpdatedAtUtc = now
            };
        }

        await conversationStore.SaveChatRunAsync(updatedRun, cancellationToken);
        await RecordAuditAsync(
            updatedRun,
            "cancel",
            "orchestration.cancel.requested",
            "AI orchestration cancellation requested",
            updatedRun.Status == AiChatRunStatus.Cancelled
                ? "The queued AI turn was cancelled before execution started."
                : "The running AI turn received a cancellation request.",
            updatedRun.Status == AiChatRunStatus.Cancelled
                ? AiExecutionOutcome.Cancelled
                : AiExecutionOutcome.Pending,
            now,
            cancellationToken);

        runQueue.Cancel(runId);
    }

    public async Task ProcessRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var run = await conversationStore.GetChatRunAsync(runId, cancellationToken);
                if (run is null || run.IsTerminal)
                {
                    return;
                }

                if (run.CancellationRequested)
                {
                    await MarkRunCancelledAsync(run, "The AI turn was cancelled.", cancellationToken);
                    return;
                }

                if (HasPendingProviderPayload(run))
                {
                    var processedRun = await ProcessPendingProviderResponseAsync(run, cancellationToken);
                    if (processedRun.IsTerminal || processedRun.Status == AiChatRunStatus.AwaitingApproval)
                    {
                        return;
                    }

                    continue;
                }

                if (run.ExecutionPlanId.HasValue)
                {
                    var continuedRun = await ProcessExecutionPlanAsync(run, cancellationToken);
                    if (continuedRun.IsTerminal || continuedRun.Status == AiChatRunStatus.AwaitingApproval)
                    {
                        return;
                    }

                    continue;
                }

                var providerRun = await RequestProviderTurnAsync(run, null, false, cancellationToken);
                if (providerRun.IsTerminal || providerRun.Status == AiChatRunStatus.AwaitingApproval)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var interruptedRun = await conversationStore.GetChatRunAsync(runId, CancellationToken.None);
            if (interruptedRun is null || interruptedRun.IsTerminal)
            {
                return;
            }

            if (interruptedRun.CancellationRequested)
            {
                await MarkRunCancelledAsync(interruptedRun, "The AI turn was cancelled.", CancellationToken.None);
                return;
            }

            await RequeueInterruptedRunAsync(interruptedRun, CancellationToken.None);
        }
        catch (Exception exception)
        {
            var failedRun = await conversationStore.GetChatRunAsync(runId, CancellationToken.None);
            if (failedRun is not null && !failedRun.IsTerminal)
            {
                await MarkRunFailedAsync(failedRun, exception.Message, CancellationToken.None);
            }
        }
    }

    private async Task<AiChatRun> RequestProviderTurnAsync(
        AiChatRun run,
        IReadOnlyList<AiProviderInputItem>? inputItems,
        bool isContinuation,
        CancellationToken cancellationToken)
    {
        var thread = await conversationStore.GetThreadAsync(run.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("That AI chat thread could not be found.");
        var userMessage = await conversationStore.GetMessageAsync(run.MessageId, cancellationToken)
            ?? throw new InvalidOperationException("The AI turn is missing its originating user message.");

        if (thread.ProviderType == AiProviderType.Unknown || string.IsNullOrWhiteSpace(thread.ProviderKey))
        {
            return await MarkRunCompletedAsync(
                run,
                "No provider is configured for this thread, so only the user message was persisted.",
                cancellationToken);
        }

        var provider = await providerRegistry.GetProviderAsync(thread.ProviderKey, cancellationToken);
        if (provider is null)
        {
            return await MarkRunFailedAsync(
                run,
                "The configured AI provider could not be loaded for this chat thread.",
                cancellationToken);
        }

        var attachedServers = AiLocalMachine.GetEffectiveAttachedServers(
            thread.Id,
            await conversationStore.ListAttachedServersAsync(thread.Id, cancellationToken));
        var messageHistory = (await conversationStore.ListMessagesAsync(thread.Id, cancellationToken))
            .OrderBy(message => message.SequenceNumber)
            .ToList();
        var continuationActions = isContinuation && run.ExecutionPlanId.HasValue
            ? (await conversationStore.GetExecutionPlanAsync(run.ExecutionPlanId.Value, cancellationToken))?.Actions ?? Array.Empty<AiProposedAction>()
            : Array.Empty<AiProposedAction>();
        var publishedTools = provider.Settings.ToolUseEnabled
            ? toolBridge.ListPublishedTools(thread, attachedServers)
            : Array.Empty<AiToolDefinition>();
        var resolvedInputs = AiProviderInputBuilder.Build(
            provider.Definition,
            thread,
            userMessage,
            messageHistory,
            inputItems,
            isContinuation,
            continuationActions,
            run.Id);

        var step = isContinuation
            ? AiChatRunStep.ContinuingProviderTurn
            : AiChatRunStep.RequestingProviderTurn;
        var summary = isContinuation
            ? "Continuing the provider turn with Linux Made Sane tool output."
            : "Sending the user turn to the provider.";
        var startedAtUtc = DateTimeOffset.UtcNow;

        run = run with
        {
            Status = AiChatRunStatus.Running,
            Step = step,
            StatusSummary = summary,
            LastError = string.Empty,
            UpdatedAtUtc = startedAtUtc
        };

        await conversationStore.SaveChatRunAsync(run, cancellationToken);
        await RecordAuditAsync(
            run,
            $"provider-start:{run.ProviderAttemptCount + 1}:{isContinuation}",
            $"provider.{thread.ProviderType.ToString().ToLowerInvariant()}.turn.started",
            "Provider turn started",
            $"{provider.Definition.DisplayName} | published tools {publishedTools.Count}.",
            AiExecutionOutcome.Pending,
            startedAtUtc,
            cancellationToken);

        AiProviderTurnResult turnResult;
        Exception? finalException = null;

        for (var attempt = run.ProviderAttemptCount + 1; attempt <= MaxProviderAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            run = run with
            {
                ProviderAttemptCount = attempt,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await conversationStore.SaveChatRunAsync(run, cancellationToken);

            try
            {
                turnResult = await provider.ExecuteTurnAsync(
                    new AiProviderTurnRequest(
                        thread,
                        messageHistory,
                        attachedServers,
                        resolvedInputs,
                        publishedTools,
                        provider.Settings.StreamingEnabled,
                        false),
                    cancellationToken: cancellationToken);

                var now = DateTimeOffset.UtcNow;
                thread = thread with
                {
                    ProviderConversationReference = provider.Definition.SupportsConversationState
                        ? turnResult.ConversationReference ?? thread.ProviderConversationReference
                        : string.Empty,
                    ProviderStateReference = provider.Definition.SupportsConversationState
                        ? turnResult.ProviderResponseId
                        : string.Empty,
                    UpdatedAtUtc = now
                };

                await conversationStore.SaveThreadAsync(thread, cancellationToken);

                run = run with
                {
                    Status = AiChatRunStatus.Running,
                    Step = AiChatRunStep.ProcessingProviderResponse,
                    StatusSummary = turnResult.ToolCalls.Count == 0
                        ? "Provider response received. Persisting assistant output."
                        : $"Provider response received with {turnResult.ToolCalls.Count} tool call(s).",
                    CurrentProviderResponseId = turnResult.ProviderResponseId,
                    PendingAssistantOutputsJson = JsonSerializer.Serialize(turnResult.AssistantOutputs, SerializerOptions),
                    PendingToolCallsJson = JsonSerializer.Serialize(turnResult.ToolCalls, SerializerOptions),
                    LastError = string.Empty,
                    UpdatedAtUtc = now
                };

                await conversationStore.SaveChatRunAsync(run, cancellationToken);
                return run;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (attempt < MaxProviderAttempts && IsTransientProviderFailure(exception))
            {
                finalException = exception;
                var retryAtUtc = DateTimeOffset.UtcNow;
                run = run with
                {
                    Status = AiChatRunStatus.Running,
                    Step = step,
                    StatusSummary = $"Provider request failed transiently. Retrying attempt {attempt + 1} of {MaxProviderAttempts}.",
                    LastError = exception.Message,
                    UpdatedAtUtc = retryAtUtc
                };

                await conversationStore.SaveChatRunAsync(run, cancellationToken);
                await RecordAuditAsync(
                    run,
                    $"provider-retry:{attempt}",
                    $"provider.{thread.ProviderType.ToString().ToLowerInvariant()}.turn.retry",
                    "Provider turn retry scheduled",
                    exception.Message,
                    AiExecutionOutcome.Pending,
                    retryAtUtc,
                    cancellationToken);

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 3)), cancellationToken);
            }
            catch (Exception exception)
            {
                finalException = exception;
                break;
            }
        }

        await RecordProviderFailureAsync(thread, userMessage.Id, finalException?.Message ?? string.Empty, cancellationToken);
        return await MarkRunFailedAsync(
            run,
            finalException?.Message ?? "The provider returned an unspecified failure.",
            cancellationToken);
    }

    private async Task<AiChatRun> ProcessPendingProviderResponseAsync(
        AiChatRun run,
        CancellationToken cancellationToken)
    {
        var thread = await conversationStore.GetThreadAsync(run.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("That AI chat thread could not be found.");
        var userMessage = await conversationStore.GetMessageAsync(run.MessageId, cancellationToken)
            ?? throw new InvalidOperationException("The AI turn is missing its originating user message.");
        var provider = await providerRegistry.GetProviderAsync(thread.ProviderKey, cancellationToken)
            ?? throw new InvalidOperationException("The configured AI provider could not be loaded.");
        var attachedServers = AiLocalMachine.GetEffectiveAttachedServers(
            thread.Id,
            await conversationStore.ListAttachedServersAsync(thread.Id, cancellationToken));
        var messageHistory = (await conversationStore.ListMessagesAsync(thread.Id, cancellationToken))
            .OrderBy(message => message.SequenceNumber)
            .ToList();
        var assistantOutputs = Deserialize<IReadOnlyList<AiProviderAssistantOutput>>(run.PendingAssistantOutputsJson) ?? [];
        var toolCalls = Deserialize<IReadOnlyList<AiProviderToolCallRequest>>(run.PendingToolCallsJson) ?? [];
        var previousProviderStateReference = thread.ProviderStateReference;

        thread = await PersistAssistantOutputsAsync(
            run,
            thread,
            assistantOutputs,
            provider.Definition.DisplayName,
            messageHistory,
            cancellationToken);

        var checkpointAtUtc = DateTimeOffset.UtcNow;
        await conversationStore.SaveCheckpointAsync(
            new AiChatCheckpoint(
                CreateDeterministicGuid($"{run.Id}:checkpoint:provider:{run.CurrentProviderResponseId}"),
                thread.Id,
                userMessage.Id,
                "Provider response",
                toolCalls.Count == 0
                    ? $"{provider.Definition.DisplayName} completed a chat turn."
                    : $"{provider.Definition.DisplayName} requested {toolCalls.Count} tool call(s).",
                JsonSerializer.Serialize(
                    new AiProviderCheckpointState(
                        provider.ProviderKey,
                        provider.Settings.ProviderType,
                        thread.ProviderConversationReference,
                        run.CurrentProviderResponseId,
                        string.Equals(previousProviderStateReference, run.CurrentProviderResponseId, StringComparison.Ordinal)
                            ? null
                            : previousProviderStateReference,
                        thread.ModelId,
                        toolCalls,
                        checkpointAtUtc),
                    SerializerOptions),
                checkpointAtUtc),
            cancellationToken);

        await RecordAuditAsync(
            run,
            $"provider-response:{run.CurrentProviderResponseId}",
            $"provider.{thread.ProviderType.ToString().ToLowerInvariant()}.response.completed",
            "Provider response completed",
            $"{provider.Definition.DisplayName} | response {run.CurrentProviderResponseId} | assistant messages {assistantOutputs.Count} | tool calls {toolCalls.Count}.",
            AiExecutionOutcome.Succeeded,
            checkpointAtUtc,
            cancellationToken);

        run = run with
        {
            PendingAssistantOutputsJson = string.Empty,
            PendingToolCallsJson = string.Empty,
            UpdatedAtUtc = checkpointAtUtc
        };

        if (toolCalls.Count == 0)
        {
            return await MarkRunCompletedAsync(
                run,
                "Assistant response persisted to the local chat history.",
                cancellationToken);
        }

        foreach (var toolCall in toolCalls)
        {
            await RecordAuditAsync(
                run,
                $"tool-call:{toolCall.ProviderToolCallId}",
                $"provider.{thread.ProviderType.ToString().ToLowerInvariant()}.tool-call.requested",
                "Provider tool call requested",
                $"{toolCall.ToolName} | call {toolCall.ProviderToolCallId}",
                AiExecutionOutcome.Pending,
                checkpointAtUtc,
                cancellationToken);
        }

        var publishedTools = provider.Settings.ToolUseEnabled
            ? toolBridge.ListPublishedTools(thread, attachedServers)
            : Array.Empty<AiToolDefinition>();
        var executionPlan = await approvalService.ProposeExecutionPlanAsync(
            thread.Id,
            BuildExecutionPlanProposal(userMessage.Id, toolCalls, attachedServers, publishedTools),
            new AiApprovalActor($"provider:{provider.ProviderKey}", AiUserTrustLevel.Standard, true),
            cancellationToken);

        if (executionPlan.Actions.Any(action => action.ApprovalRequirement == AiApprovalRequirement.Blocked))
        {
            run = run with
            {
                Status = AiChatRunStatus.Failed,
                Step = AiChatRunStep.Failed,
                StatusSummary = "One or more requested actions were blocked by Linux Made Sane policy.",
                ExecutionPlanId = executionPlan.Id,
                LastError = "A requested tool call was blocked by the current trust policy.",
                UpdatedAtUtc = checkpointAtUtc,
                CompletedAtUtc = checkpointAtUtc
            };

            await conversationStore.SaveChatRunAsync(run, cancellationToken);
            await RecordAuditAsync(
                run,
                $"blocked:{executionPlan.Id}",
                "orchestration.blocked",
                "AI orchestration blocked",
                "At least one provider-requested action was blocked by Linux Made Sane policy.",
                AiExecutionOutcome.Rejected,
                checkpointAtUtc,
                cancellationToken);

            return run;
        }

        if (executionPlan.Actions.Any(action => action.RequiresApproval))
        {
            run = run with
            {
                Status = AiChatRunStatus.AwaitingApproval,
                Step = AiChatRunStep.AwaitingApproval,
                StatusSummary = $"{executionPlan.Actions.Count(action => action.RequiresApproval)} action(s) are waiting for approval.",
                ExecutionPlanId = executionPlan.Id,
                UpdatedAtUtc = checkpointAtUtc
            };

            await conversationStore.SaveChatRunAsync(run, cancellationToken);
            await RecordAuditAsync(
                run,
                $"awaiting-approval:{executionPlan.Id}",
                "provider.tool-call.awaiting-approval",
                "Provider tool calls are awaiting approval",
                $"{toolCalls.Count} tool call(s) require approval before Linux Made Sane can continue this turn.",
                AiExecutionOutcome.Pending,
                checkpointAtUtc,
                cancellationToken);

            return run;
        }

        run = run with
        {
            Status = AiChatRunStatus.Running,
            Step = AiChatRunStep.ExecutingApprovedTools,
            StatusSummary = $"Executing {executionPlan.Actions.Count} approved Linux Made Sane tool action(s).",
            ExecutionPlanId = executionPlan.Id,
            UpdatedAtUtc = checkpointAtUtc
        };

        await conversationStore.SaveChatRunAsync(run, cancellationToken);
        return run;
    }

    private async Task<AiChatRun> ProcessExecutionPlanAsync(AiChatRun run, CancellationToken cancellationToken)
    {
        if (!run.ExecutionPlanId.HasValue)
        {
            return run;
        }

        var thread = await conversationStore.GetThreadAsync(run.ThreadId, cancellationToken)
            ?? throw new InvalidOperationException("That AI chat thread could not be found.");
        var userMessage = await conversationStore.GetMessageAsync(run.MessageId, cancellationToken)
            ?? throw new InvalidOperationException("The AI turn is missing its originating user message.");
        var executionPlan = await conversationStore.GetExecutionPlanAsync(run.ExecutionPlanId.Value, cancellationToken)
            ?? throw new InvalidOperationException("The current AI execution plan could not be found.");
        var approvalRequests = (await conversationStore.ListApprovalRequestsAsync(thread.Id, cancellationToken))
            .Where(request => request.ExecutionPlanId == executionPlan.Id)
            .ToArray();

        if (approvalRequests.Any(request => request.State is AiApprovalState.Blocked or AiApprovalState.Denied))
        {
            var rejectedPlan = executionPlan with
            {
                Outcome = AiExecutionOutcome.Rejected,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Actions = executionPlan.Actions
                    .Select(action => approvalRequests.Any(request => request.ProposedActionId == action.Id &&
                                                                     request.State is AiApprovalState.Blocked or AiApprovalState.Denied)
                        ? action with { Outcome = AiExecutionOutcome.Rejected }
                        : action)
                    .ToArray()
            };

            await conversationStore.SaveExecutionPlanAsync(rejectedPlan, cancellationToken);
            return await MarkRunFailedAsync(
                run with { ExecutionPlanId = rejectedPlan.Id },
                "An approval request was denied or blocked, so this AI turn cannot continue.",
                cancellationToken);
        }

        if (approvalRequests.Any(request => request.State == AiApprovalState.Pending))
        {
            var waitingRun = run with
            {
                Status = AiChatRunStatus.AwaitingApproval,
                Step = AiChatRunStep.AwaitingApproval,
                StatusSummary = $"{approvalRequests.Count(request => request.State == AiApprovalState.Pending)} action(s) are waiting for approval.",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            await conversationStore.SaveChatRunAsync(waitingRun, cancellationToken);
            return waitingRun;
        }

        var messageHistory = (await conversationStore.ListMessagesAsync(thread.Id, cancellationToken))
            .OrderBy(message => message.SequenceNumber)
            .ToList();
        var actionResults = new Dictionary<Guid, AiToolResult>();
        var updatedActions = executionPlan.Actions.ToDictionary(action => action.Id);

        foreach (var action in executionPlan.Actions.OrderBy(action => action.SequenceNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (run.CancellationRequested)
            {
                return await MarkRunCancelledAsync(run, "The AI turn was cancelled before all approved tools finished executing.", cancellationToken);
            }

            var existingInvocation = await conversationStore.GetToolInvocationByProposedActionIdAsync(action.Id, cancellationToken);
            if (existingInvocation?.Result is not null)
            {
                updatedActions[action.Id] = action with { Outcome = existingInvocation.Result.Outcome };
                actionResults[action.Id] = existingInvocation.Result;
                thread = await EnsureToolMessagePersistedAsync(run, thread, action, existingInvocation.Result, messageHistory, cancellationToken);
                continue;
            }

            if (existingInvocation is not null && existingInvocation.Status == AiInvocationStatus.Running)
            {
                var staleResult = await PersistStaleInvocationFailureAsync(run, thread, action, existingInvocation, messageHistory, cancellationToken);
                updatedActions[action.Id] = action with { Outcome = staleResult.Outcome };
                actionResults[action.Id] = staleResult;
                continue;
            }

            var startedAtUtc = DateTimeOffset.UtcNow;
            run = run with
            {
                Status = AiChatRunStatus.Running,
                Step = AiChatRunStep.ExecutingApprovedTools,
                StatusSummary = $"Executing {action.ToolName}.",
                UpdatedAtUtc = startedAtUtc
            };

            await conversationStore.SaveChatRunAsync(run, cancellationToken);

            var invocation = new AiToolInvocation(
                CreateDeterministicGuid($"{action.Id}:invocation"),
                thread.Id,
                userMessage.Id,
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
                run,
                $"tool-start:{action.Id}",
                "tool.invocation.started",
                "Tool invocation started",
                $"{action.ToolName} | {action.Title}",
                AiExecutionOutcome.Pending,
                startedAtUtc,
                cancellationToken);

            AiToolResult persistedResult;
            AiInvocationStatus invocationStatus;

            try
            {
                var executionResult = await toolBridge.InvokeAsync(invocation, cancellationToken);
                persistedResult = executionResult.PersistedResult with
                {
                    Id = CreateDeterministicGuid($"{invocation.Id}:result")
                };
                invocationStatus = MapInvocationStatus(persistedResult.Outcome);
                await conversationStore.SaveToolResultAsync(persistedResult, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                persistedResult = new AiToolResult(
                    CreateDeterministicGuid($"{invocation.Id}:result"),
                    invocation.Id,
                    AiExecutionOutcome.Cancelled,
                    $"{action.ToolName} was cancelled before Linux Made Sane could finish execution.",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    DateTimeOffset.UtcNow);
                invocationStatus = AiInvocationStatus.Cancelled;
                await conversationStore.SaveToolResultAsync(persistedResult, cancellationToken);
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
                await conversationStore.SaveToolResultAsync(persistedResult, cancellationToken);
            }

            var completedInvocation = invocation with
            {
                Status = invocationStatus,
                CompletedAtUtc = persistedResult.CompletedAtUtc,
                Result = persistedResult
            };

            await conversationStore.SaveToolInvocationAsync(completedInvocation, cancellationToken);

            updatedActions[action.Id] = action with
            {
                Outcome = persistedResult.Outcome
            };
            actionResults[action.Id] = persistedResult;

            await RecordAuditAsync(
                run,
                $"tool-finish:{action.Id}",
                persistedResult.Outcome switch
                {
                    AiExecutionOutcome.Succeeded => "tool.invocation.completed",
                    AiExecutionOutcome.Cancelled => "tool.invocation.cancelled",
                    _ => "tool.invocation.failed"
                },
                persistedResult.Outcome switch
                {
                    AiExecutionOutcome.Succeeded => "Tool invocation completed",
                    AiExecutionOutcome.Cancelled => "Tool invocation cancelled",
                    _ => "Tool invocation failed"
                },
                $"{action.ToolName} | {persistedResult.Summary}",
                persistedResult.Outcome,
                persistedResult.CompletedAtUtc,
                cancellationToken);

            thread = await EnsureToolMessagePersistedAsync(run, thread, action, persistedResult, messageHistory, cancellationToken);

            if (persistedResult.Outcome == AiExecutionOutcome.Cancelled)
            {
                var cancelledPlan = executionPlan with
                {
                    Outcome = AiExecutionOutcome.Cancelled,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Actions = executionPlan.Actions
                        .Select(planAction => updatedActions[planAction.Id])
                        .ToArray()
                };

                await conversationStore.SaveExecutionPlanAsync(cancelledPlan, cancellationToken);
                return await MarkRunCancelledAsync(run, "The AI turn was cancelled while Linux Made Sane was executing a tool.", cancellationToken);
            }
        }

        var finalActions = executionPlan.Actions
            .Select(action => updatedActions[action.Id])
            .ToArray();
        var finalPlan = executionPlan with
        {
            Outcome = BuildPlanOutcome(finalActions),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Actions = finalActions
        };

        await conversationStore.SaveExecutionPlanAsync(finalPlan, cancellationToken);

        var toolOutputs = finalPlan.Actions
            .OrderBy(action => action.SequenceNumber)
            .Where(action => actionResults.ContainsKey(action.Id))
            .Select(action => new AiProviderToolOutputInputItem(
                action.ProviderToolCallId,
                action.ToolName,
                BuildToolOutputJson(action.ToolName, actionResults[action.Id])))
            .Cast<AiProviderInputItem>()
            .ToArray();

        if (toolOutputs.Length == 0)
        {
            return await MarkRunCompletedAsync(
                run with { ExecutionPlanId = finalPlan.Id },
                "No tool output was produced, so the AI turn ended without a provider continuation.",
                cancellationToken);
        }

        return await RequestProviderTurnAsync(
            run with
            {
                Status = AiChatRunStatus.Running,
                Step = AiChatRunStep.ContinuingProviderTurn,
                StatusSummary = $"Continuing the provider turn with {toolOutputs.Length} Linux Made Sane tool result(s).",
                ExecutionPlanId = finalPlan.Id,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            },
            toolOutputs,
            true,
            cancellationToken);
    }

    private async Task<AiToolResult> PersistStaleInvocationFailureAsync(
        AiChatRun run,
        AiChatThread thread,
        AiProposedAction action,
        AiToolInvocation invocation,
        List<AiChatMessage> messageHistory,
        CancellationToken cancellationToken)
    {
        var staleResult = new AiToolResult(
            CreateDeterministicGuid($"{invocation.Id}:result"),
            invocation.Id,
            AiExecutionOutcome.Failed,
            $"{action.ToolName} did not finish before orchestration resumed, so Linux Made Sane marked it as failed rather than running it twice.",
            string.Empty,
            "The prior tool invocation was still marked as running when orchestration resumed.",
            string.Empty,
            null,
            DateTimeOffset.UtcNow);

        await conversationStore.SaveToolResultAsync(staleResult, cancellationToken);
        await conversationStore.SaveToolInvocationAsync(invocation with
        {
            Status = AiInvocationStatus.Failed,
            CompletedAtUtc = staleResult.CompletedAtUtc,
            Result = staleResult
        }, cancellationToken);

        await RecordAuditAsync(
            run,
            $"tool-stale:{action.Id}",
            "tool.invocation.failed",
            "Tool invocation marked failed after orchestration resume",
            $"{action.ToolName} | {staleResult.Summary}",
            staleResult.Outcome,
            staleResult.CompletedAtUtc,
            cancellationToken);

        await EnsureToolMessagePersistedAsync(run, thread, action, staleResult, messageHistory, cancellationToken);
        return staleResult;
    }

    private async Task<AiChatThread> PersistAssistantOutputsAsync(
        AiChatRun run,
        AiChatThread thread,
        IReadOnlyList<AiProviderAssistantOutput> assistantOutputs,
        string providerDisplayName,
        List<AiChatMessage> messageHistory,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < assistantOutputs.Count; index++)
        {
            var assistantOutput = assistantOutputs[index];
            if (string.IsNullOrWhiteSpace(assistantOutput.Content))
            {
                continue;
            }

            var messageId = CreateDeterministicGuid($"{run.Id}:assistant:{run.CurrentProviderResponseId}:{index}");
            if (messageHistory.Any(message => message.Id == messageId))
            {
                continue;
            }

            var createdAtUtc = DateTimeOffset.UtcNow;
            var assistantMessage = new AiChatMessage(
                messageId,
                thread.Id,
                GetNextSequenceNumber(messageHistory),
                AiChatMessageRole.Assistant,
                assistantOutput.Content.Trim(),
                createdAtUtc);

            thread = await PersistMessageAsync(
                thread,
                assistantMessage,
                CreateDeterministicGuid($"{run.Id}:audit:assistant:{messageId}"),
                "message.assistant.created",
                "Assistant message saved",
                $"{providerDisplayName} assistant message #{assistantMessage.SequenceNumber} saved to the local thread history.",
                cancellationToken);

            messageHistory.Add(assistantMessage);
        }

        return thread;
    }

    private async Task<AiChatThread> EnsureToolMessagePersistedAsync(
        AiChatRun run,
        AiChatThread thread,
        AiProposedAction action,
        AiToolResult result,
        List<AiChatMessage> messageHistory,
        CancellationToken cancellationToken)
    {
        var messageId = CreateDeterministicGuid($"{run.Id}:tool-message:{action.Id}");
        if (messageHistory.Any(message => message.Id == messageId))
        {
            return thread;
        }

        var toolMessage = new AiChatMessage(
            messageId,
            thread.Id,
            GetNextSequenceNumber(messageHistory),
            AiChatMessageRole.Tool,
            BuildToolOutputJson(action.ToolName, result),
            result.CompletedAtUtc);

        thread = await PersistMessageAsync(
            thread,
            toolMessage,
            CreateDeterministicGuid($"{run.Id}:audit:tool-message:{action.Id}"),
            "message.tool.created",
            "Tool output saved",
            $"{action.ToolName} | {result.Summary}",
            cancellationToken);

        messageHistory.Add(toolMessage);
        return thread;
    }

    private async Task<AiChatThread> PersistMessageAsync(
        AiChatThread thread,
        AiChatMessage message,
        Guid auditEntryId,
        string eventType,
        string summary,
        string details,
        CancellationToken cancellationToken)
    {
        await conversationStore.SaveMessageAsync(message, cancellationToken);
        var updatedThread = thread with { UpdatedAtUtc = message.CreatedAtUtc };
        await conversationStore.SaveThreadAsync(updatedThread, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                auditEntryId,
                thread.Id,
                message.Id,
                eventType,
                summary,
                details,
                AiExecutionOutcome.Succeeded,
                message.CreatedAtUtc),
            cancellationToken);
        return updatedThread;
    }

    private async Task<AiChatRun> MarkRunCompletedAsync(
        AiChatRun run,
        string summary,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var completedRun = run with
        {
            Status = AiChatRunStatus.Completed,
            Step = AiChatRunStep.Completed,
            StatusSummary = summary,
            PendingAssistantOutputsJson = string.Empty,
            PendingToolCallsJson = string.Empty,
            LastError = string.Empty,
            UpdatedAtUtc = now,
            CompletedAtUtc = now
        };

        await conversationStore.SaveChatRunAsync(completedRun, cancellationToken);
        await RecordAuditAsync(
            completedRun,
            "completed",
            "orchestration.completed",
            "AI orchestration completed",
            summary,
            AiExecutionOutcome.Succeeded,
            now,
            cancellationToken);

        return completedRun;
    }

    private async Task<AiChatRun> MarkRunFailedAsync(
        AiChatRun run,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var failedRun = run with
        {
            Status = AiChatRunStatus.Failed,
            Step = AiChatRunStep.Failed,
            StatusSummary = "AI orchestration failed. Review the saved error details.",
            LastError = string.IsNullOrWhiteSpace(errorMessage)
                ? "The orchestration service returned an unspecified failure."
                : errorMessage.Trim(),
            PendingAssistantOutputsJson = string.Empty,
            PendingToolCallsJson = string.Empty,
            UpdatedAtUtc = now,
            CompletedAtUtc = now
        };

        await conversationStore.SaveChatRunAsync(failedRun, cancellationToken);
        await RecordAuditAsync(
            failedRun,
            "failed",
            "orchestration.failed",
            "AI orchestration failed",
            failedRun.LastError,
            AiExecutionOutcome.Failed,
            now,
            cancellationToken);

        return failedRun;
    }

    private async Task<AiChatRun> MarkRunCancelledAsync(
        AiChatRun run,
        string summary,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cancelledRun = run with
        {
            Status = AiChatRunStatus.Cancelled,
            Step = AiChatRunStep.Cancelled,
            StatusSummary = summary,
            PendingAssistantOutputsJson = string.Empty,
            PendingToolCallsJson = string.Empty,
            UpdatedAtUtc = now,
            CompletedAtUtc = now
        };

        await conversationStore.SaveChatRunAsync(cancelledRun, cancellationToken);
        await RecordAuditAsync(
            cancelledRun,
            "cancelled",
            "orchestration.cancelled",
            "AI orchestration cancelled",
            summary,
            AiExecutionOutcome.Cancelled,
            now,
            cancellationToken);

        return cancelledRun;
    }

    private async Task RequeueInterruptedRunAsync(AiChatRun run, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var requeuedRun = run with
        {
            Status = AiChatRunStatus.Queued,
            StatusSummary = "Execution was interrupted. The AI turn is queued to resume safely.",
            UpdatedAtUtc = now
        };

        await conversationStore.SaveChatRunAsync(requeuedRun, cancellationToken);
        await RecordAuditAsync(
            requeuedRun,
            "requeued",
            "orchestration.requeued",
            "AI orchestration requeued",
            "Execution was interrupted before completion, so Linux Made Sane queued the run to resume.",
            AiExecutionOutcome.Pending,
            now,
            cancellationToken);
    }

    private async Task RecordAuditAsync(
        AiChatRun run,
        string token,
        string eventType,
        string summary,
        string details,
        AiExecutionOutcome outcome,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await auditService.RecordAsync(
            new AiAuditEntry(
                CreateDeterministicGuid($"{run.Id}:audit:{token}"),
                run.ThreadId,
                run.MessageId,
                eventType,
                summary,
                details,
                outcome,
                createdAtUtc),
            cancellationToken);
    }

    private async Task RecordProviderFailureAsync(
        AiChatThread thread,
        Guid? messageId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await auditService.RecordAsync(
            new AiAuditEntry(
                CreateDeterministicGuid($"{thread.Id}:provider-failure:{thread.ProviderStateReference}:{messageId}"),
                thread.Id,
                messageId,
                $"provider.{thread.ProviderType.ToString().ToLowerInvariant()}.turn.failed",
                "Provider turn failed",
                string.IsNullOrWhiteSpace(errorMessage)
                    ? "The provider returned an unspecified failure."
                    : errorMessage,
                AiExecutionOutcome.Failed,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private static bool HasPendingProviderPayload(AiChatRun run) =>
        !string.IsNullOrWhiteSpace(run.PendingAssistantOutputsJson) ||
        !string.IsNullOrWhiteSpace(run.PendingToolCallsJson);

    private static int GetNextSequenceNumber(IReadOnlyList<AiChatMessage> messageHistory) =>
        messageHistory.Count == 0
            ? 1
            : messageHistory.Max(message => message.SequenceNumber) + 1;

    private static bool IsTransientProviderFailure(Exception exception)
    {
        if (exception is HttpRequestException or TimeoutException)
        {
            return true;
        }

        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var transientMarkers = new[]
        {
            "timeout",
            "timed out",
            "rate limit",
            "429",
            "502",
            "503",
            "504",
            "temporarily",
            "temporary",
            "server error",
            "connection reset"
        };

        return transientMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
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

    private static AiExecutionPlanProposal BuildExecutionPlanProposal(
        Guid messageId,
        IReadOnlyList<AiProviderToolCallRequest> toolCalls,
        IReadOnlyList<AiAttachedServer> attachedServers,
        IReadOnlyList<AiToolDefinition> publishedTools)
    {
        var toolDefinitions = publishedTools.ToDictionary(
            tool => tool.Name,
            tool => tool,
            StringComparer.OrdinalIgnoreCase);

        return new AiExecutionPlanProposal
        {
            MessageId = messageId,
            Summary = toolCalls.Count == 1
                ? "The provider requested one Linux Made Sane tool action."
                : $"The provider requested {toolCalls.Count} Linux Made Sane tool actions.",
            Actions = toolCalls
                .Select(toolCall => BuildActionProposal(toolCall, attachedServers, toolDefinitions))
                .ToList()
        };
    }

    private static AiProposedActionProposal BuildActionProposal(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        IReadOnlyDictionary<string, AiToolDefinition> toolDefinitions)
    {
        if (!toolDefinitions.TryGetValue(toolCall.ToolName, out var definition))
        {
            throw new InvalidOperationException($"The provider requested the unpublished tool {toolCall.ToolName}.");
        }

        return toolCall.ToolName switch
        {
            AiToolNames.ListServers => new AiProposedActionProposal
            {
                Title = "List available Linux servers",
                Description = "Return the Linux servers visible to this chat thread.",
                ToolName = toolCall.ToolName,
                ProviderToolCallId = toolCall.ProviderToolCallId,
                ToolArgumentsJson = toolCall.ArgumentsJson,
                RiskLevel = definition.Approval.RiskLevel
            },
            AiToolNames.GetServerSummary => BuildServerSummaryAction(toolCall, attachedServers, definition),
            AiToolNames.GetServerHealth => BuildServerHealthAction(toolCall, attachedServers, definition),
            AiToolNames.ListServices => BuildListServicesAction(toolCall, attachedServers, definition),
            AiToolNames.RestartService => BuildRestartServiceAction(toolCall, attachedServers, definition),
            AiToolNames.BrowseDirectory => BuildBrowseDirectoryAction(toolCall, attachedServers, definition),
            AiToolNames.ReadFile => BuildReadFileAction(toolCall, attachedServers, definition),
            AiToolNames.RunCommand => BuildRunCommandAction(toolCall, attachedServers, definition),
            AiToolNames.WriteFileWithConfirmation => BuildWriteFileAction(toolCall, attachedServers, definition),
            AiToolNames.InstallPackageWithConfirmation => BuildInstallPackageAction(toolCall, attachedServers, definition),
            _ => throw new InvalidOperationException($"No approval proposal mapping is defined for tool {toolCall.ToolName}.")
        };
    }

    private static AiProposedActionProposal BuildServerSummaryAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<GetServerSummaryToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);

        return new AiProposedActionProposal
        {
            Title = $"Get saved server summary for {serverName}",
            Description = $"Load the saved Linux Made Sane summary for {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildServerHealthAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<GetServerHealthToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);

        return new AiProposedActionProposal
        {
            Title = $"Collect live health from {serverName}",
            Description = $"Gather a lightweight live health snapshot from {serverName} over SSH.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            CommandPreview = "Collect hostname, uptime, load, memory, and root-disk metrics over SSH.",
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildListServicesAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<ListServicesToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);
        var filterSummary = string.IsNullOrWhiteSpace(request.Filter)
            ? "all services"
            : $"services matching '{request.Filter.Trim()}'";

        return new AiProposedActionProposal
        {
            Title = $"List {filterSummary} on {serverName}",
            Description = $"List systemd services on {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            CommandPreview = AiCommandDisplayFormatter.BuildCommandPreview(toolCall.ToolName, toolCall.ArgumentsJson),
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildRestartServiceAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<RestartServiceToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);
        var serviceName = request.ServiceName.Trim();

        return new AiProposedActionProposal
        {
            Title = $"Restart {serviceName} on {serverName}",
            Description = $"Restart the systemd unit {serviceName} on {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            CommandPreview = AiCommandDisplayFormatter.BuildCommandPreview(toolCall.ToolName, toolCall.ArgumentsJson),
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildBrowseDirectoryAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<BrowseDirectoryToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);

        return new AiProposedActionProposal
        {
            Title = $"Browse {request.Path.Trim()} on {serverName}",
            Description = $"Browse the remote directory {request.Path.Trim()} on {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildReadFileAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<ReadFileToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);

        return new AiProposedActionProposal
        {
            Title = $"Read {request.Path.Trim()} on {serverName}",
            Description = $"Read the remote file {request.Path.Trim()} on {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildRunCommandAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<RunCommandToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);

        return new AiProposedActionProposal
        {
            Title = $"Run command on {serverName}",
            Description = $"Run a shell command on {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            CommandPreview = AiCommandDisplayFormatter.BuildCommandPreview(toolCall.ToolName, toolCall.ArgumentsJson),
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildWriteFileAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<WriteFileWithConfirmationToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);

        return new AiProposedActionProposal
        {
            Title = $"Write {request.Path.Trim()} on {serverName}",
            Description = $"Write {request.Content.Length} byte(s) to {request.Path.Trim()} on {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            CommandPreview = request.CreateDirectories
                ? $"Write file {request.Path.Trim()} and create missing directories."
                : $"Write file {request.Path.Trim()}.",
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static AiProposedActionProposal BuildInstallPackageAction(
        AiProviderToolCallRequest toolCall,
        IReadOnlyList<AiAttachedServer> attachedServers,
        AiToolDefinition definition)
    {
        var request = DeserializeRequest<InstallPackageWithConfirmationToolRequest>(toolCall.ArgumentsJson);
        var serverName = ResolveServerName(request.ServerId, attachedServers);
        var packages = request.PackageNames.Select(packageName => packageName.Trim()).ToArray();

        return new AiProposedActionProposal
        {
            Title = $"Install packages on {serverName}",
            Description = $"Install {string.Join(", ", packages)} on {serverName}.",
            ToolName = toolCall.ToolName,
            ProviderToolCallId = toolCall.ProviderToolCallId,
            ToolArgumentsJson = toolCall.ArgumentsJson,
            CommandPreview = AiCommandDisplayFormatter.BuildCommandPreview(toolCall.ToolName, toolCall.ArgumentsJson),
            RiskLevel = definition.Approval.RiskLevel
        };
    }

    private static string ResolveServerName(Guid serverId, IReadOnlyList<AiAttachedServer> attachedServers) =>
        attachedServers
            .FirstOrDefault(server => server.ManagedHostId == serverId)?.ServerName
        ?? (AiLocalMachine.IsLocalMachine(serverId) ? AiLocalMachine.Name : null)
        ?? serverId.ToString();

    private static TRequest DeserializeRequest<TRequest>(string json)
    {
        var value = JsonSerializer.Deserialize<TRequest>(json, SerializerOptions);
        return value ?? throw new InvalidOperationException($"The tool arguments for {typeof(TRequest).Name} could not be deserialized.");
    }

    private static string BuildToolOutputJson(string toolName, AiToolResult result)
    {
        Dictionary<string, object?> envelope = new(StringComparer.Ordinal)
        {
            ["toolName"] = toolName,
            ["succeeded"] = result.Outcome == AiExecutionOutcome.Succeeded,
            ["summary"] = result.Summary,
            ["outputText"] = string.IsNullOrWhiteSpace(result.OutputText) ? null : result.OutputText,
            ["errorText"] = string.IsNullOrWhiteSpace(result.ErrorText) ? null : result.ErrorText,
            ["exitCode"] = result.ExitCode
        };

        if (!string.IsNullOrWhiteSpace(result.PayloadJson))
        {
            if (TryParseJsonElement(result.PayloadJson, out var payload))
            {
                envelope["payload"] = payload;
            }
            else
            {
                envelope["payloadText"] = result.PayloadJson;
            }
        }

        return JsonSerializer.Serialize(envelope, SerializerOptions);
    }

    private static bool TryParseJsonElement(string json, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static AiInvocationStatus MapInvocationStatus(AiExecutionOutcome outcome) => outcome switch
    {
        AiExecutionOutcome.Succeeded => AiInvocationStatus.Succeeded,
        AiExecutionOutcome.Pending => AiInvocationStatus.Pending,
        AiExecutionOutcome.Cancelled => AiInvocationStatus.Cancelled,
        _ => AiInvocationStatus.Failed
    };
}
