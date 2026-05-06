using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;
using System.Text.Json;

namespace LinuxMadeSane.Application.Services;

public sealed class AiApprovalService(
    IAiConversationStore conversationStore,
    IAiApprovalPolicyService approvalPolicyService,
    IAiSafeChangeService safeChangeService,
    IAiAuditService auditService,
    IAiExecutionPlanExecutionService executionPlanExecutionService,
    IAiChatRunQueue runQueue,
    IAiProviderRegistry providerRegistry,
    IAiProviderCapabilityService capabilityService) : IAiApprovalService
{
    public AiApprovalService(
        IAiConversationStore conversationStore,
        IAiApprovalPolicyService approvalPolicyService,
        IAiSafeChangeService safeChangeService,
        IAiAuditService auditService,
        IAiExecutionPlanExecutionService executionPlanExecutionService,
        IAiChatRunQueue runQueue)
        : this(
            conversationStore,
            approvalPolicyService,
            safeChangeService,
            auditService,
            executionPlanExecutionService,
            runQueue,
            new NoOpAiProviderRegistry(),
            new NoOpAiProviderCapabilityService())
    {
    }

    public AiApprovalService(
        IAiConversationStore conversationStore,
        IAiApprovalPolicyService approvalPolicyService,
        IAiAuditService auditService,
        IAiExecutionPlanExecutionService executionPlanExecutionService,
        IAiChatRunQueue runQueue)
        : this(
            conversationStore,
            approvalPolicyService,
            new NoOpAiSafeChangeService(),
            auditService,
            executionPlanExecutionService,
            runQueue,
            new NoOpAiProviderRegistry(),
            new NoOpAiProviderCapabilityService())
    {
    }

    public async Task<AiExecutionPlan> ProposeExecutionPlanAsync(
        Guid threadId,
        AiExecutionPlanProposal proposal,
        AiApprovalActor actor,
        CancellationToken cancellationToken = default)
    {
        ValidateProposal(proposal, actor);

        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken);
        if (thread is null)
        {
            throw new InvalidOperationException("That AI chat thread could not be found.");
        }

        var now = DateTimeOffset.UtcNow;
        var planId = Guid.NewGuid();
        var actions = new List<AiProposedAction>(proposal.Actions.Count);
        var approvals = new List<AiApprovalRequest>();
        var providerCapability = await GetProviderCapabilityAsync(thread, cancellationToken);

        for (var index = 0; index < proposal.Actions.Count; index++)
        {
            var actionProposal = proposal.Actions[index];
            var safeChange = await safeChangeService.AnalyzeAsync(threadId, actionProposal, cancellationToken);
            var actionId = Guid.NewGuid();
            var provisionalAction = new AiProposedAction(
                actionId,
                planId,
                index + 1,
                actionProposal.Title.Trim(),
                actionProposal.Description.Trim(),
                actionProposal.ToolName.Trim(),
                actionProposal.ProviderToolCallId.Trim(),
                actionProposal.ToolArgumentsJson.Trim(),
                actionProposal.CommandPreview.Trim(),
                actionProposal.RiskLevel,
                AiApprovalRequirement.AutoRun,
                AiUserTrustLevel.Standard,
                string.Empty,
                AiExecutionOutcome.Pending)
            {
                SafeChange = safeChange
            };

            var evaluation = approvalPolicyService.Evaluate(
                thread.TrustProfile,
                actor.TrustLevel,
                provisionalAction,
                actor.AdminOverrideExists);
            evaluation = ApplyProviderCapabilityGuardrails(evaluation, provisionalAction, providerCapability);

            var action = provisionalAction with
            {
                ApprovalRequirement = evaluation.Requirement,
                RequiredTrustLevel = evaluation.RequiredTrustLevel,
                PolicyReason = evaluation.Reason,
                Outcome = evaluation.Requirement == AiApprovalRequirement.Blocked
                    ? AiExecutionOutcome.Rejected
                    : AiExecutionOutcome.Pending
            };

            actions.Add(action);

            if (evaluation.Requirement == AiApprovalRequirement.AutoRun)
            {
                continue;
            }

            approvals.Add(approvalPolicyService.CreateApprovalRequest(thread, action, evaluation, now));
        }

        var plan = new AiExecutionPlan(
            planId,
            threadId,
            proposal.MessageId,
            proposal.Summary.Trim(),
            actions.Any(action => action.Outcome == AiExecutionOutcome.Rejected)
                ? AiExecutionOutcome.Rejected
                : AiExecutionOutcome.Pending,
            now,
            now,
            actions);

        await conversationStore.SaveExecutionPlanAsync(plan, cancellationToken);
        await conversationStore.SaveThreadAsync(thread with { UpdatedAtUtc = now }, cancellationToken);

        foreach (var request in approvals)
        {
            await conversationStore.SaveApprovalRequestAsync(request, cancellationToken);
            await auditService.RecordAsync(
                BuildApprovalAuditEntry(request, now),
                cancellationToken);
        }

        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                threadId,
                proposal.MessageId,
                "execution-plan.proposed",
                "AI execution plan proposed",
                BuildPlanAuditDetails(plan, actor),
                plan.Outcome == AiExecutionOutcome.Rejected
                    ? AiExecutionOutcome.Rejected
                    : AiExecutionOutcome.Pending,
                now)
            {
                MetadataJson = JsonSerializer.Serialize(
                    new
                    {
                        actor = actor.ActorName,
                        actions = plan.Actions.Select(action => new
                        {
                            action.Id,
                            action.Title,
                            action.ToolName,
                            action.RiskLevel,
                            action.SafeChange
                        })
                    })
            },
            cancellationToken);

        return plan;
    }

    public async Task DecideApprovalAsync(
        Guid requestId,
        AiApprovalDecisionCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateDecisionCommand(command);

        var request = await conversationStore.GetApprovalRequestAsync(requestId, cancellationToken);
        if (request is null)
        {
            throw new InvalidOperationException("That approval request could not be found.");
        }

        var now = DateTimeOffset.UtcNow;
        var state = command.DecisionType == AiApprovalDecisionType.Deny
            ? AiApprovalState.Denied
            : AiApprovalState.Approved;
        var rememberDecision = command.DecisionType == AiApprovalDecisionType.ApproveOnce
            ? false
            : command.RememberDecision && request.RememberDecisionSupported;

        var decision = new AiApprovalDecision(
            state,
            command.DecisionType,
            command.ActorName.Trim(),
            command.UserTrustLevel,
            command.AdminOverrideUsed,
            rememberDecision,
            command.Reason.Trim(),
            now);

        approvalPolicyService.EnsureDecisionAllowed(request, decision);

        var updatedRequest = request with
        {
            State = state,
            Decision = decision
        };

        await conversationStore.SaveApprovalRequestAsync(updatedRequest, cancellationToken);

        var thread = await conversationStore.GetThreadAsync(request.ThreadId, cancellationToken);
        if (thread is not null)
        {
            await conversationStore.SaveThreadAsync(thread with { UpdatedAtUtc = now }, cancellationToken);
        }

        await auditService.RecordAsync(
            BuildDecisionAuditEntry(updatedRequest, decision, now),
            cancellationToken);

        if (!request.ExecutionPlanId.HasValue)
        {
            return;
        }

        var executionPlan = await conversationStore.GetExecutionPlanAsync(request.ExecutionPlanId.Value, cancellationToken);
        if (executionPlan is null)
        {
            return;
        }

        var updatedPlan = executionPlan with
        {
            Outcome = decision.State == AiApprovalState.Denied
                ? AiExecutionOutcome.Rejected
                : executionPlan.Outcome,
            UpdatedAtUtc = now,
            Actions = executionPlan.Actions
                .Select(action => action.Id == request.ProposedActionId && decision.State == AiApprovalState.Denied
                    ? action with { Outcome = AiExecutionOutcome.Rejected }
                    : action)
                .ToArray()
        };

        await conversationStore.SaveExecutionPlanAsync(updatedPlan, cancellationToken);

        var planRequests = (await conversationStore.ListApprovalRequestsAsync(request.ThreadId, cancellationToken))
            .Where(item => item.ExecutionPlanId == updatedPlan.Id)
            .ToArray();
        var associatedRun = await conversationStore.GetChatRunByExecutionPlanIdAsync(updatedPlan.Id, cancellationToken);
        if (associatedRun is null)
        {
            if (decision.State == AiApprovalState.Approved &&
                !planRequests.Any(item => item.State == AiApprovalState.Pending))
            {
                await executionPlanExecutionService.ExecuteApprovedPlanAsync(updatedPlan.Id, cancellationToken);
            }

            return;
        }

        if (associatedRun.IsTerminal)
        {
            return;
        }

        if (decision.State == AiApprovalState.Denied)
        {
            var failedRun = associatedRun with
            {
                Status = AiChatRunStatus.Failed,
                Step = AiChatRunStep.Failed,
                StatusSummary = "Approval was denied, so this AI turn cannot continue.",
                LastError = string.IsNullOrWhiteSpace(decision.Reason)
                    ? "Approval denied."
                    : decision.Reason.Trim(),
                UpdatedAtUtc = now,
                CompletedAtUtc = now
            };

            await conversationStore.SaveChatRunAsync(failedRun, cancellationToken);
            await auditService.RecordAsync(
                new AiAuditEntry(
                    Guid.NewGuid(),
                    failedRun.ThreadId,
                    failedRun.MessageId,
                    "orchestration.failed.after-denial",
                    "AI orchestration stopped after approval denial",
                    failedRun.LastError,
                    AiExecutionOutcome.Rejected,
                    now),
                cancellationToken);

            return;
        }

        if (planRequests.Any(item => item.State == AiApprovalState.Pending))
        {
            var waitingRun = associatedRun with
            {
                Status = AiChatRunStatus.AwaitingApproval,
                Step = AiChatRunStep.AwaitingApproval,
                StatusSummary = $"{planRequests.Count(item => item.State == AiApprovalState.Pending)} action(s) are still waiting for approval.",
                UpdatedAtUtc = now
            };

            await conversationStore.SaveChatRunAsync(waitingRun, cancellationToken);
            return;
        }

        var queuedRun = associatedRun with
        {
            Status = AiChatRunStatus.Queued,
            Step = AiChatRunStep.ExecutingApprovedTools,
            StatusSummary = "Approvals granted. Queued to resume Linux Made Sane tool execution.",
            UpdatedAtUtc = now
        };

        await conversationStore.SaveChatRunAsync(queuedRun, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                queuedRun.ThreadId,
                queuedRun.MessageId,
                "orchestration.resumed.after-approval",
                "AI orchestration queued after approval",
                "All approval gates for the current execution plan have been satisfied.",
                AiExecutionOutcome.Pending,
                now),
            cancellationToken);

        await runQueue.EnqueueAsync(queuedRun.Id, cancellationToken);
    }

    private static void ValidateProposal(AiExecutionPlanProposal proposal, AiApprovalActor actor)
    {
        if (proposal.Actions.Count == 0)
        {
            throw new InvalidOperationException("At least one proposed action is required.");
        }

        if (string.IsNullOrWhiteSpace(proposal.Summary))
        {
            throw new InvalidOperationException("A plan summary is required.");
        }

        if (string.IsNullOrWhiteSpace(actor.ActorName))
        {
            throw new InvalidOperationException("An approval actor name is required.");
        }

        foreach (var action in proposal.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Title))
            {
                throw new InvalidOperationException("Each proposed action needs a title.");
            }

            if (string.IsNullOrWhiteSpace(action.ToolName))
            {
                throw new InvalidOperationException("Each proposed action needs a tool name.");
            }
        }
    }

    private static void ValidateDecisionCommand(AiApprovalDecisionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ActorName))
        {
            throw new InvalidOperationException("Reviewer name is required.");
        }
    }

    private static AiAuditEntry BuildApprovalAuditEntry(AiApprovalRequest request, DateTimeOffset now)
    {
        var eventType = request.State == AiApprovalState.Blocked
            ? "approval.blocked"
            : "approval.requested";
        var summary = request.State == AiApprovalState.Blocked
            ? "AI action blocked by approval policy"
            : "Approval request created";
        var outcome = request.State == AiApprovalState.Blocked
            ? AiExecutionOutcome.Rejected
            : AiExecutionOutcome.Pending;

        return new AiAuditEntry(
            Guid.NewGuid(),
            request.ThreadId,
            null,
            eventType,
            summary,
            $"{request.Title} | risk {request.RiskLevel} | requirement {request.Requirement} | {request.PolicyReason}",
            outcome,
            now);
    }

    private static AiAuditEntry BuildDecisionAuditEntry(
        AiApprovalRequest request,
        AiApprovalDecision decision,
        DateTimeOffset now)
    {
        var eventType = decision.State == AiApprovalState.Approved
            ? decision.DecisionType == AiApprovalDecisionType.ApproveOnce
                ? "approval.approved.once"
                : "approval.approved"
            : "approval.denied";
        var summary = decision.State == AiApprovalState.Approved
            ? "Approval granted"
            : "Approval denied";
        var details = $"{request.Title} | decided by {decision.DecidedBy} ({decision.DecidedByTrustLevel})";

        if (!string.IsNullOrWhiteSpace(decision.Reason))
        {
            details = $"{details} | reason: {decision.Reason}";
        }

        if (decision.AdminOverrideUsed)
        {
            details = $"{details} | admin override used";
        }

        if (decision.RememberDecision)
        {
            details = $"{details} | remember decision requested";
        }

        return new AiAuditEntry(
            Guid.NewGuid(),
            request.ThreadId,
            null,
            eventType,
            summary,
            details,
            decision.State == AiApprovalState.Approved
                ? AiExecutionOutcome.Succeeded
                : AiExecutionOutcome.Rejected,
            now);
    }

    private static string BuildPlanAuditDetails(AiExecutionPlan plan, AiApprovalActor actor) =>
        $"{plan.Actions.Count} action(s) proposed by {actor.ActorName} with reviewer trust {actor.TrustLevel}.";

    private async Task<ProviderCapabilityContext?> GetProviderCapabilityAsync(
        AiChatThread thread,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(thread.ProviderKey))
        {
            return null;
        }

        try
        {
            var provider = await providerRegistry.GetProviderAsync(thread.ProviderKey, cancellationToken);
            if (provider is null)
            {
                return null;
            }

            var modelId = string.IsNullOrWhiteSpace(thread.ModelId)
                ? provider.Settings.DefaultModelId
                : thread.ModelId.Trim();
            var report = await capabilityService.AssessAsync(provider.Settings, modelId, cancellationToken);
            return new ProviderCapabilityContext(provider.Settings.DisplayName, modelId, report);
        }
        catch
        {
            return null;
        }
    }

    private static AiApprovalEvaluation ApplyProviderCapabilityGuardrails(
        AiApprovalEvaluation evaluation,
        AiProposedAction action,
        ProviderCapabilityContext? providerCapability)
    {
        if (providerCapability is null)
        {
            return evaluation;
        }

        var providerReason = BuildProviderReason(providerCapability);
        if (action.RiskLevel == AiActionRiskLevel.ReadOnly)
        {
            return evaluation with
            {
                Reason = AppendReason(evaluation.Reason, providerReason)
            };
        }

        if (!providerCapability.Report.RequiresExtraApprovalForMutations)
        {
            return evaluation with
            {
                Reason = AppendReason(evaluation.Reason, providerReason)
            };
        }

        var requirement = evaluation.Requirement;
        var requiredTrustLevel = evaluation.RequiredTrustLevel;

        if (requirement is AiApprovalRequirement.AutoRun or AiApprovalRequirement.UserConfirmation)
        {
            requirement = AiApprovalRequirement.UserConfirmation;
            requiredTrustLevel = MaxTrustLevel(requiredTrustLevel, AiUserTrustLevel.Trusted);
        }

        return new AiApprovalEvaluation(
            requirement,
            requiredTrustLevel,
            AppendReason(
                evaluation.Reason,
                $"Provider {providerCapability.ProviderLabel} / {providerCapability.ModelId} is capability-limited for mutating Deep Fix work. {providerCapability.Report.Warning}"));
    }

    private static AiUserTrustLevel MaxTrustLevel(AiUserTrustLevel left, AiUserTrustLevel right) =>
        (AiUserTrustLevel)Math.Max((int)left, (int)right);

    private static string BuildProviderReason(ProviderCapabilityContext providerCapability) =>
        string.IsNullOrWhiteSpace(providerCapability.Report.Warning)
            ? $"Provider {providerCapability.ProviderLabel} / {providerCapability.ModelId}."
            : $"Provider {providerCapability.ProviderLabel} / {providerCapability.ModelId}. {providerCapability.Report.Warning}";

    private static string AppendReason(string reason, string detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? reason
            : string.IsNullOrWhiteSpace(reason)
                ? detail
                : $"{reason} {detail}";

    private sealed record ProviderCapabilityContext(
        string ProviderLabel,
        string ModelId,
        LocalAiCapabilityReport Report);

    private sealed class NoOpAiProviderCapabilityService : IAiProviderCapabilityService
    {
        public Task<LocalAiCapabilityReport> AssessAsync(
            AiProviderSettings settings,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalAiCapabilityReport(
                settings.DisplayName,
                modelId,
                AiProviderCapabilityFlag.BasicChat,
                false,
                false,
                string.Empty,
                string.Empty));
    }

    private sealed class NoOpAiProviderRegistry : IAiProviderRegistry
    {
        public IReadOnlyList<AiProviderDefinition> ListSupportedProviders() => [];
        public IReadOnlyList<AiProviderModelOption> ListModelCatalog(AiProviderType? providerType = null) => [];
        public Task<IReadOnlyList<AiProviderSettings>> ListConfiguredProvidersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AiProviderSettings>>([]);
        public Task<IReadOnlyList<AiModelDefinition>> ListModelsAsync(string? providerKey = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AiModelDefinition>>([]);
        public AiProviderDefinition? FindDefinition(AiProviderType providerType) => null;
        public Task<IAiProvider?> GetProviderAsync(string providerKey, CancellationToken cancellationToken = default) => Task.FromResult<IAiProvider?>(null);
    }
}
