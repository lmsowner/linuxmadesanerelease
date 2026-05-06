using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Services;

public sealed class AiChatService(
    IAiConversationStore conversationStore,
    IManagedHostStore hostStore,
    IAiProviderRegistry providerRegistry,
    IAiAuditService auditService,
    IAiToolBridge toolBridge,
    IAiSafeChangeService safeChangeService,
    IAiApprovalService approvalService,
    IAiExecutionPlanExecutionService executionPlanExecutionService,
    IAiChatOrchestrationService orchestrationService) : IAiChatService
{
    public AiChatService(
        IAiConversationStore conversationStore,
        IManagedHostStore hostStore,
        IAiProviderRegistry providerRegistry,
        IAiAuditService auditService,
        IAiToolBridge toolBridge,
        IAiApprovalService approvalService,
        IAiExecutionPlanExecutionService executionPlanExecutionService,
        IAiChatOrchestrationService orchestrationService)
        : this(
            conversationStore,
            hostStore,
            providerRegistry,
            auditService,
            toolBridge,
            new NoOpAiSafeChangeService(),
            approvalService,
            executionPlanExecutionService,
            orchestrationService)
    {
    }

    public async Task<AiChatWorkspaceViewModel?> GetWorkspaceAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken);
        if (thread is null)
        {
            return null;
        }

        var messages = await conversationStore.ListMessagesAsync(threadId, cancellationToken);
        var attachedServers = AiLocalMachine.GetEffectiveAttachedServers(
            threadId,
            await conversationStore.ListAttachedServersAsync(threadId, cancellationToken));
        var chatRuns = await conversationStore.ListChatRunsAsync(threadId, cancellationToken);
        var executionPlans = await conversationStore.ListExecutionPlansAsync(threadId, cancellationToken);
        var approvalRequests = await conversationStore.ListApprovalRequestsAsync(threadId, cancellationToken);
        var toolInvocations = await conversationStore.ListToolInvocationsAsync(threadId, cancellationToken);
        var auditEntries = await auditService.ListAsync(threadId, cancellationToken);
        var checkpoints = await conversationStore.ListCheckpointsAsync(threadId, cancellationToken);
        var availableServers = await hostStore.ListAsync(cancellationToken);
        var supportedProviders = providerRegistry.ListSupportedProviders();
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var models = await providerRegistry.ListModelsAsync(cancellationToken: cancellationToken);
        var publishedTools = toolBridge.ListPublishedTools(thread, attachedServers)
            .Select(tool => tool.Name)
            .ToArray();

        return new AiChatWorkspaceViewModel(
            thread,
            messages,
            attachedServers,
            chatRuns,
            executionPlans,
            approvalRequests,
            toolInvocations,
            auditEntries,
            checkpoints,
            availableServers,
            supportedProviders,
            AiProviderViewModelMapper.Map(configuredProviders),
            models,
            publishedTools)
        {
            Timeline = AiChatTimelineViewModelMapper.Map(
                messages,
                attachedServers,
                executionPlans,
                approvalRequests,
                toolInvocations)
        };
    }

    public async Task SendUserMessageAsync(
        Guid threadId,
        AiChatMessageComposer composer,
        CancellationToken cancellationToken = default)
    {
        ValidateComposer(composer);

        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken);
        if (thread is null)
        {
            throw new InvalidOperationException("That AI chat thread could not be found.");
        }

        var existingMessages = await conversationStore.ListMessagesAsync(threadId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var nextSequenceNumber = existingMessages.Count == 0
            ? 1
            : existingMessages.Max(message => message.SequenceNumber) + 1;

        var message = new AiChatMessage(
            Guid.NewGuid(),
            threadId,
            nextSequenceNumber,
            AiChatMessageRole.User,
            composer.Content.Trim(),
            now);

        await conversationStore.SaveMessageAsync(message, cancellationToken);
        await conversationStore.SaveThreadAsync(thread with { UpdatedAtUtc = now }, cancellationToken);

        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                threadId,
                message.Id,
                "message.user.created",
                "User message saved",
                BuildMessageAuditDetails(thread, message),
                AiExecutionOutcome.Succeeded,
                now),
            cancellationToken);

        await orchestrationService.QueueUserTurnAsync(
            thread with { UpdatedAtUtc = now },
            message,
            cancellationToken);
    }

    public async Task ReRunCommandAsync(Guid threadId, Guid invocationId, CancellationToken cancellationToken = default)
    {
        var thread = await EnsureManualActionReadyAsync(threadId, cancellationToken);
        var invocation = await GetCommandInvocationAsync(threadId, invocationId, cancellationToken);
        var attachedServers = AiLocalMachine.GetEffectiveAttachedServers(
            threadId,
            await conversationStore.ListAttachedServersAsync(threadId, cancellationToken));
        var definition = toolBridge.FindTool(invocation.ToolName)
            ?? throw new InvalidOperationException($"Tool {invocation.ToolName} is not available for rerun.");
        var targetServerName = ResolveTargetServerName(invocation, attachedServers);
        var commandPreview = ResolveDisplayCommandText(invocation);
        var now = DateTimeOffset.UtcNow;

        var plan = await approvalService.ProposeExecutionPlanAsync(
            threadId,
            new AiExecutionPlanProposal
            {
                MessageId = invocation.MessageId,
                Summary = $"Manual rerun requested for {invocation.ToolName}.",
                Actions =
                [
                    new AiProposedActionProposal
                    {
                        Title = BuildManualRerunTitle(invocation.ToolName, targetServerName),
                        Description = string.IsNullOrWhiteSpace(commandPreview)
                            ? "Re-run the saved Linux Made Sane command from chat history."
                            : $"Re-run the saved Linux Made Sane command from chat history: {commandPreview}",
                        ToolName = invocation.ToolName,
                        ProviderToolCallId = $"manual-rerun:{invocation.Id}:{Guid.NewGuid():N}",
                        ToolArgumentsJson = invocation.ArgumentsJson,
                        CommandPreview = commandPreview,
                        RiskLevel = definition.Approval.RiskLevel
                    }
                ]
            },
            new AiApprovalActor("manual-rerun", AiUserTrustLevel.Standard, true),
            cancellationToken);

        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                threadId,
                invocation.MessageId,
                "tool.rerun.requested",
                "Command rerun requested",
                string.IsNullOrWhiteSpace(commandPreview)
                    ? $"{invocation.ToolName} rerun requested."
                    : $"{invocation.ToolName} rerun requested | {commandPreview}",
                plan.Actions.Any(action => action.RequiresApproval)
                    ? AiExecutionOutcome.Pending
                    : AiExecutionOutcome.Succeeded,
                now),
            cancellationToken);

        await conversationStore.SaveThreadAsync(thread with { UpdatedAtUtc = now }, cancellationToken);

        if (plan.Actions.Any(action => action.Outcome == AiExecutionOutcome.Rejected))
        {
            throw new InvalidOperationException("The saved command cannot be rerun under the current thread trust policy.");
        }

        if (!plan.Actions.Any(action => action.RequiresApproval))
        {
            await executionPlanExecutionService.ExecuteApprovedPlanAsync(plan.Id, cancellationToken);
        }
    }

    public async Task RequestRollbackAsync(Guid threadId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var thread = await EnsureManualActionReadyAsync(threadId, cancellationToken);
        var proposal = await safeChangeService.CreateRollbackProposalAsync(threadId, actionId, cancellationToken);
        var definition = toolBridge.FindTool(proposal.ToolName)
            ?? throw new InvalidOperationException($"Tool {proposal.ToolName} is not available for rollback.");
        var now = DateTimeOffset.UtcNow;
        proposal.RiskLevel = definition.Approval.RiskLevel;

        var plan = await approvalService.ProposeExecutionPlanAsync(
            threadId,
            new AiExecutionPlanProposal
            {
                Summary = $"Manual rollback requested for {proposal.Title}.",
                Actions = [proposal]
            },
            new AiApprovalActor("manual-rollback", AiUserTrustLevel.Standard, true),
            cancellationToken);

        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                threadId,
                null,
                "tool.rollback.requested",
                "Rollback requested",
                $"{proposal.Title} requested.",
                plan.Actions.Any(action => action.RequiresApproval)
                    ? AiExecutionOutcome.Pending
                    : AiExecutionOutcome.Succeeded,
                now),
            cancellationToken);

        await conversationStore.SaveThreadAsync(thread with { UpdatedAtUtc = now }, cancellationToken);

        if (plan.Actions.Any(action => action.Outcome == AiExecutionOutcome.Rejected))
        {
            throw new InvalidOperationException("That rollback cannot run under the current chat trust policy.");
        }

        if (!plan.Actions.Any(action => action.RequiresApproval))
        {
            await executionPlanExecutionService.ExecuteApprovedPlanAsync(plan.Id, cancellationToken);
        }
    }

    public async Task AskAiToRetryCommandAsync(Guid threadId, Guid invocationId, CancellationToken cancellationToken = default)
    {
        await EnsureManualActionReadyAsync(threadId, cancellationToken);
        var invocation = await GetCommandInvocationAsync(threadId, invocationId, cancellationToken);
        var attachedServers = AiLocalMachine.GetEffectiveAttachedServers(
            threadId,
            await conversationStore.ListAttachedServersAsync(threadId, cancellationToken));
        var targetServerName = ResolveTargetServerName(invocation, attachedServers);
        var commandPreview = ResolveDisplayCommandText(invocation);
        var previousOutcome = invocation.Result?.Outcome switch
        {
            AiExecutionOutcome.Failed => invocation.Result.ExitCode.HasValue
                ? $"The last attempt failed with exit code {invocation.Result.ExitCode.Value}."
                : "The last attempt failed.",
            AiExecutionOutcome.Succeeded => "The last attempt completed successfully.",
            AiExecutionOutcome.Cancelled => "The last attempt was cancelled.",
            _ => "The previous attempt did not finish."
        };

        var content = string.IsNullOrWhiteSpace(targetServerName)
            ? $"Retry this command if it is still the right next step:{Environment.NewLine}{commandPreview}"
            : $"Retry this command on {targetServerName} if it is still the right next step:{Environment.NewLine}{commandPreview}";
        content = $"{content}{Environment.NewLine}{Environment.NewLine}{previousOutcome}";

        await SendUserMessageAsync(
            threadId,
            new AiChatMessageComposer
            {
                Content = content
            },
            cancellationToken);
    }

    public async Task RemoveMessageAsync(
        Guid threadId,
        Guid messageId,
        bool truncateFromMessage = false,
        CancellationToken cancellationToken = default)
    {
        var thread = await EnsureManualActionReadyAsync(threadId, cancellationToken);
        var message = await conversationStore.GetMessageAsync(messageId, cancellationToken);
        if (message is null || message.ThreadId != threadId)
        {
            throw new InvalidOperationException("That chat message could not be found.");
        }

        var now = DateTimeOffset.UtcNow;
        await conversationStore.RemoveMessageAsync(threadId, messageId, truncateFromMessage, cancellationToken);
        await conversationStore.SaveThreadAsync(ResetProviderState(thread, now), cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                threadId,
                truncateFromMessage ? null : messageId,
                truncateFromMessage ? "conversation.truncated" : "message.removed",
                truncateFromMessage ? "Conversation truncated" : "Message removed",
                truncateFromMessage
                    ? $"Conversation truncated from message #{message.SequenceNumber}."
                    : $"Removed message #{message.SequenceNumber}.",
                AiExecutionOutcome.Succeeded,
                now),
            cancellationToken);
    }

    public async Task ClearConversationAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        var thread = await EnsureManualActionReadyAsync(threadId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await conversationStore.ClearConversationAsync(threadId, cancellationToken);
        await conversationStore.SaveThreadAsync(ResetProviderState(thread, now), cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                threadId,
                null,
                "conversation.cleared",
                "Conversation cleared",
                "Removed all persisted messages, runs, approvals, tool executions, checkpoints, and related audit history from this thread.",
                AiExecutionOutcome.Succeeded,
                now),
            cancellationToken);
    }

    public Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default) =>
        orchestrationService.RequestCancellationAsync(runId, cancellationToken);

    private async Task<AiChatThread> EnsureManualActionReadyAsync(Guid threadId, CancellationToken cancellationToken)
    {
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That AI chat thread could not be found.");
        var activeRun = await conversationStore.GetActiveChatRunForThreadAsync(threadId, cancellationToken);
        if (activeRun is not null)
        {
            throw new InvalidOperationException("Finish or cancel the active AI turn before changing conversation history or rerunning commands.");
        }

        return thread;
    }

    private async Task<AiToolInvocation> GetCommandInvocationAsync(Guid threadId, Guid invocationId, CancellationToken cancellationToken)
    {
        var invocation = (await conversationStore.ListToolInvocationsAsync(threadId, cancellationToken))
            .FirstOrDefault(item => item.Id == invocationId)
            ?? throw new InvalidOperationException("That command execution record could not be found.");

        if (!AiCommandDisplayFormatter.IsCommandExecutionTool(invocation.ToolName))
        {
            throw new InvalidOperationException("Only command-style tool executions can be retried from the chat history.");
        }

        return invocation;
    }

    private static void ValidateComposer(AiChatMessageComposer composer)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(composer);
        var isValid = Validator.TryValidateObject(composer, context, results, true);

        if (isValid)
        {
            return;
        }

        throw new InvalidOperationException(string.Join(" ", results.Select(result => result.ErrorMessage)));
    }

    private static string BuildMessageAuditDetails(AiChatThread thread, AiChatMessage message)
    {
        var providerSummary = string.IsNullOrWhiteSpace(thread.ModelId)
            ? thread.ProviderType == AiProviderType.Unknown
                ? "No provider configured"
                : thread.ProviderType.ToString()
            : $"{thread.ProviderType} / {thread.ModelId}";

        return $"Message #{message.SequenceNumber} saved to {thread.Title}. Provider context: {providerSummary}.";
    }

    private static AiChatThread ResetProviderState(AiChatThread thread, DateTimeOffset updatedAtUtc) =>
        thread with
        {
            ProviderConversationReference = string.Empty,
            ProviderStateReference = string.Empty,
            UpdatedAtUtc = updatedAtUtc
        };

    private static string BuildManualRerunTitle(string toolName, string targetServerName) => toolName switch
    {
        AiToolNames.RunCommand => string.IsNullOrWhiteSpace(targetServerName)
            ? "Re-run command"
            : $"Re-run command on {targetServerName}",
        AiToolNames.ListServices => string.IsNullOrWhiteSpace(targetServerName)
            ? "Re-run service listing"
            : $"Re-run service listing on {targetServerName}",
        AiToolNames.RestartService => string.IsNullOrWhiteSpace(targetServerName)
            ? "Re-run service restart"
            : $"Re-run service restart on {targetServerName}",
        AiToolNames.InstallPackageWithConfirmation => string.IsNullOrWhiteSpace(targetServerName)
            ? "Re-run package install"
            : $"Re-run package install on {targetServerName}",
        _ => string.IsNullOrWhiteSpace(targetServerName)
            ? $"Re-run {toolName}"
            : $"Re-run {toolName} on {targetServerName}"
    };

    private static string ResolveTargetServerName(
        AiToolInvocation invocation,
        IReadOnlyList<AiAttachedServer> attachedServers)
    {
        var serverId = AiCommandDisplayFormatter.ResolveTargetServerId(invocation.ToolName, invocation.ArgumentsJson);
        if (!serverId.HasValue)
        {
            return string.Empty;
        }

        return attachedServers
            .FirstOrDefault(server => server.ManagedHostId == serverId.Value)?.ServerName
            ?? (AiLocalMachine.IsLocalMachine(serverId.Value) ? AiLocalMachine.Name : null)
            ?? serverId.Value.ToString();
    }

    private static string ResolveDisplayCommandText(AiToolInvocation invocation)
    {
        var preview = AiCommandDisplayFormatter.BuildCommandPreview(invocation.ToolName, invocation.ArgumentsJson);
        if (!string.IsNullOrWhiteSpace(preview))
        {
            return preview;
        }

        return AiCommandDisplayFormatter.SanitizeDisplayText(invocation.Result?.PayloadJson ?? string.Empty);
    }
}
