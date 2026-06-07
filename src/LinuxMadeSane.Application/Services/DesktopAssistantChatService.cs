// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LinuxMadeSane.Application.Contracts.DesktopAssistant;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.Application.Services;

public sealed partial class DesktopAssistantChatService(
    IAiConversationStore conversationStore,
    IAiProviderRegistry providerRegistry,
    IAiAuditService auditService,
    IDesktopSessionBroker desktopSessionBroker,
    ICommandExecutionService commandExecutionService,
    IAiPromptSanitizer promptSanitizer) : IDesktopAssistantChatService
{
    private const string ThreadTitlePrefix = "Desktop Assistant";
    private const int MaxHistoryEntries = 14;
    private const int MaxHistoryRecapChars = 2200;
    private const int MaxHistoryEntryChars = 320;
    private const int MaxPromptChars = 4000;
    private const int MaxDesktopProcessRows = 32;
    private const int MaxSystemProcessRows = 12;
    private const int MaxProcessCommandChars = 150;
    private const string AptSourcesListDirectory = "/etc/apt/sources.list.d/";
    private const string AptDisabledSourcesDirectory = "/etc/apt/lms-disabled-sources";
    private static readonly TimeSpan DesktopActionTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DesktopEvidenceRefreshTimeout = TimeSpan.FromSeconds(8);

    public async Task<DesktopAssistantChatWorkspaceViewModel> GetWorkspaceAsync(
        Guid? sessionId,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        CancellationToken cancellationToken = default)
    {
        desktopSnapshot = desktopSessionBroker.GetSnapshot();
        var sessions = await ListSessionsAsync(cancellationToken);
        var activeSessionId = ResolveActiveSessionId(sessionId, sessions);
        var activeThread = activeSessionId.HasValue
            ? await conversationStore.GetThreadAsync(activeSessionId.Value, cancellationToken)
            : null;
        var messages = activeSessionId.HasValue
            ? await conversationStore.ListMessagesAsync(activeSessionId.Value, cancellationToken)
            : [];
        var provider = await ResolveProviderAsync(activeThread?.ProviderKey, false, cancellationToken);
        var providerKey = activeThread?.ProviderKey ?? provider?.ProviderKey ?? string.Empty;
        var modelId = string.IsNullOrWhiteSpace(activeThread?.ModelId)
            ? provider?.Settings.DefaultModelId ?? string.Empty
            : activeThread.ModelId;

        var proposedFix = TryRecoverPendingFixFromMessages(messages);

        return new DesktopAssistantChatWorkspaceViewModel(
            sessions,
            activeSessionId,
            messages,
            desktopSnapshot.BestAvailableSession is not null,
            provider is not null,
            providerKey,
            provider?.Settings.DisplayName ?? "No AI provider configured",
            modelId,
            BuildStatusSummary(desktopSnapshot),
            proposedFix);
    }

    public async Task<Guid> CreateSessionAsync(
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveProviderAsync(providerKey, true, cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, provider.Settings.DefaultModelId);

        var now = DateTimeOffset.UtcNow;
        var thread = new AiChatThread(
            Guid.NewGuid(),
            $"{ThreadTitlePrefix} - {now.ToLocalTime():g}",
            provider.ProviderKey,
            provider.Definition.ProviderType,
            selectedModelId,
            new AiTrustProfile(
                AiTrustLevel.Guided,
                AllowReadOnlyTools: true,
                AllowMutatingTools: false,
                RequireApprovalForMediumRisk: true,
                RequireApprovalForHighRisk: true),
            string.Empty,
            string.Empty,
            now,
            now);

        await conversationStore.SaveThreadAsync(thread, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                null,
                "desktop-assistant.chat.created",
                "Desktop Assistant chat session created",
                $"Provider: {provider.Settings.DisplayName}; model: {thread.ModelId}",
                AiExecutionOutcome.Succeeded,
                now),
            cancellationToken);

        return thread.Id;
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> DeleteSessionAsync(
        Guid sessionId,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        CancellationToken cancellationToken = default)
    {
        var thread = await conversationStore.GetThreadAsync(sessionId, cancellationToken);
        if (thread is null)
        {
            return await GetWorkspaceAsync(null, desktopSnapshot, cancellationToken);
        }

        if (!IsDesktopAssistantThread(thread))
        {
            throw new InvalidOperationException("That chat session does not belong to Desktop Assistant.");
        }

        await conversationStore.DeleteThreadAsync(sessionId, cancellationToken);
        return await GetWorkspaceAsync(null, desktopSnapshot, cancellationToken);
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> SendMessageAsync(
        Guid? sessionId,
        string message,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        desktopSnapshot = desktopSessionBroker.GetSnapshot();
        if (desktopSnapshot.BestAvailableSession is null)
        {
            throw new InvalidOperationException("Desktop Assistant is not ready. Connect the desktop helper before chatting to the desktop.");
        }

        desktopSnapshot = await RefreshDesktopEvidenceAsync(desktopSnapshot, cancellationToken);

        var threadId = sessionId ?? await CreateSessionAsync(providerKey, modelId, cancellationToken);
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That Desktop Assistant chat session could not be found.");
        if (!IsDesktopAssistantThread(thread))
        {
            throw new InvalidOperationException("That chat session does not belong to Desktop Assistant.");
        }

        var provider = await ResolveProviderAsync(
                string.IsNullOrWhiteSpace(providerKey) ? thread.ProviderKey : providerKey,
                true,
                cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, string.IsNullOrWhiteSpace(thread.ModelId) ? provider.Settings.DefaultModelId : thread.ModelId);

        var userText = NormalizeMessage(message);
        var requestKind = ClassifyRequest(userText);
        var existingMessages = await conversationStore.ListMessagesAsync(thread.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var nextSequenceNumber = GetNextSequenceNumber(existingMessages);
        var userMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            nextSequenceNumber,
            AiChatMessageRole.User,
            userText,
            now);

        await conversationStore.SaveMessageAsync(userMessage, cancellationToken);
        existingMessages = [.. existingMessages, userMessage];

        var directProposedFix = LooksLikeDirectFixRequest(userText)
            ? TryBuildSupportedActionFallback(userText, string.Empty, existingMessages)
            : null;
        if (directProposedFix is not null)
        {
            var directAssistantMessage = new AiChatMessage(
                Guid.NewGuid(),
                thread.Id,
                nextSequenceNumber + 1,
                AiChatMessageRole.Assistant,
                BuildProposedFixApprovalMessage(directProposedFix),
                DateTimeOffset.UtcNow);
            await conversationStore.SaveMessageAsync(directAssistantMessage, cancellationToken);

            await conversationStore.SaveThreadAsync(thread with
            {
                ProviderKey = provider.ProviderKey,
                ProviderType = provider.Definition.ProviderType,
                ModelId = selectedModelId,
                ProviderConversationReference = string.Empty,
                ProviderStateReference = string.Empty,
                UpdatedAtUtc = directAssistantMessage.CreatedAtUtc
            }, cancellationToken);

            await auditService.RecordAsync(
                new AiAuditEntry(
                    Guid.NewGuid(),
                    thread.Id,
                    userMessage.Id,
                    "desktop-assistant.chat.proposed-fix",
                    "Desktop Assistant proposed an approved LMS action",
                    $"Supported action: {directProposedFix.Kind}; desktop context: {BuildStatusSummary(desktopSnapshot)}",
                    AiExecutionOutcome.Succeeded,
                    directAssistantMessage.CreatedAtUtc),
                cancellationToken);

            var directWorkspace = await GetWorkspaceAsync(thread.Id, desktopSnapshot, cancellationToken);
            return directWorkspace with { ProposedFix = directProposedFix };
        }

        var providerThread = thread with
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.Definition.ProviderType,
            ModelId = selectedModelId,
            ProviderConversationReference = string.Empty,
            ProviderStateReference = string.Empty,
            TrustProfile = thread.TrustProfile with { AllowMutatingTools = false }
        };

        var prompt = BuildPrompt(userText, requestKind, desktopSnapshot, BuildConversationRecap(existingMessages));
        var sanitization = promptSanitizer.Sanitize(prompt, provider.Settings.ProviderType);
        var assistantText = await ExecuteProviderTextAsync(
            provider,
            providerThread,
            existingMessages,
            sanitization.Content,
            cancellationToken);
        if (requestKind == DesktopAssistantRequestKind.ReadOnlyQuestion &&
            ViolatesReadOnlyResponseContract(assistantText))
        {
            var retryPrompt = BuildReadOnlyContractRetryPrompt(userText, desktopSnapshot, assistantText);
            var retrySanitization = promptSanitizer.Sanitize(retryPrompt, provider.Settings.ProviderType);
            assistantText = await ExecuteProviderTextAsync(
                provider,
                providerThread,
                existingMessages,
                retrySanitization.Content,
                cancellationToken);
        }

        if (requestKind == DesktopAssistantRequestKind.ReadOnlyQuestion &&
            ViolatesReadOnlyResponseContract(assistantText))
        {
            assistantText = BuildReadOnlyGuardFallback(desktopSnapshot);
        }

        var proposedFix = TryBuildSupportedActionFallback(userText, assistantText, existingMessages);
        if (proposedFix is not null)
        {
            assistantText = BuildProposedFixApprovalMessage(proposedFix);
        }
        else if (LooksLikePendingFixAssistantMessage(assistantText))
        {
            assistantText = BuildUnsupportedApprovalFallback();
        }

        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new InvalidOperationException("The AI provider returned no assistant text.");
        }

        var assistantMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            nextSequenceNumber + 1,
            AiChatMessageRole.Assistant,
            assistantText,
            DateTimeOffset.UtcNow);
        await conversationStore.SaveMessageAsync(assistantMessage, cancellationToken);

        await conversationStore.SaveThreadAsync(providerThread with
        {
            ProviderConversationReference = string.Empty,
            ProviderStateReference = string.Empty,
            UpdatedAtUtc = assistantMessage.CreatedAtUtc
        }, cancellationToken);

        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                userMessage.Id,
                "desktop-assistant.chat.turn",
                "Desktop Assistant chat turn completed",
                $"Kind: {requestKind}; desktop context: {BuildStatusSummary(desktopSnapshot)}; sanitization: {sanitization.Summary}",
                AiExecutionOutcome.Succeeded,
                assistantMessage.CreatedAtUtc),
            cancellationToken);

        var workspace = await GetWorkspaceAsync(thread.Id, desktopSnapshot, cancellationToken);
        return workspace with { ProposedFix = proposedFix };
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> ApplyKeyboardLayoutAsync(
        Guid? sessionId,
        string layout,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var session = desktopSnapshot.BestAvailableSession
            ?? throw new InvalidOperationException("Desktop Assistant is not ready. Connect the desktop helper before applying desktop fixes.");
        var normalizedLayout = NormalizeKeyboardLayout(layout);
        var threadId = sessionId ?? await CreateSessionAsync(providerKey, modelId, cancellationToken);
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That Desktop Assistant chat session could not be found.");
        if (!IsDesktopAssistantThread(thread))
        {
            throw new InvalidOperationException("That chat session does not belong to Desktop Assistant.");
        }

        var provider = await ResolveProviderAsync(
                string.IsNullOrWhiteSpace(providerKey) ? thread.ProviderKey : providerKey,
                true,
                cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, string.IsNullOrWhiteSpace(thread.ModelId) ? provider.Settings.DefaultModelId : thread.ModelId);
        var existingMessages = await conversationStore.ListMessagesAsync(thread.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var userMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            GetNextSequenceNumber(existingMessages),
            AiChatMessageRole.User,
            $"Deep Fix: set keyboard layout to {normalizedLayout}.",
            now);
        await conversationStore.SaveMessageAsync(userMessage, cancellationToken);

        var request = new DesktopSessionActionRequest(
            Guid.NewGuid(),
            DesktopSessionActionKinds.SetKeyboardLayout,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["layout"] = normalizedLayout
            },
            DateTimeOffset.UtcNow);
        var actionResult = await desktopSessionBroker.ExecuteActionAsync(
            session.ConnectionId,
            request,
            DesktopActionTimeout,
            cancellationToken);

        var assistantMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            userMessage.SequenceNumber + 1,
            AiChatMessageRole.Assistant,
            BuildKeyboardLayoutActionMessage(normalizedLayout, actionResult),
            DateTimeOffset.UtcNow);
        await conversationStore.SaveMessageAsync(assistantMessage, cancellationToken);

        await conversationStore.SaveThreadAsync(thread with
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.Definition.ProviderType,
            ModelId = selectedModelId,
            UpdatedAtUtc = assistantMessage.CreatedAtUtc
        }, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                userMessage.Id,
                "desktop-assistant.keyboard-layout.applied",
                actionResult.Summary,
                actionResult.Detail,
                actionResult.Succeeded ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
                assistantMessage.CreatedAtUtc),
            cancellationToken);

        return await GetWorkspaceAsync(thread.Id, desktopSnapshot, cancellationToken);
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> InstallAptPackagesAsync(
        Guid? sessionId,
        IReadOnlyList<string> packageNames,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPackageNames = NormalizePackageNames(packageNames);
        var packageList = string.Join(", ", normalizedPackageNames);
        var threadId = sessionId ?? await CreateSessionAsync(providerKey, modelId, cancellationToken);
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That Desktop Assistant chat session could not be found.");
        if (!IsDesktopAssistantThread(thread))
        {
            throw new InvalidOperationException("That chat session does not belong to Desktop Assistant.");
        }

        var provider = await ResolveProviderAsync(
                string.IsNullOrWhiteSpace(providerKey) ? thread.ProviderKey : providerKey,
                true,
                cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, string.IsNullOrWhiteSpace(thread.ModelId) ? provider.Settings.DefaultModelId : thread.ModelId);
        var existingMessages = await conversationStore.ListMessagesAsync(thread.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var userMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            GetNextSequenceNumber(existingMessages),
            AiChatMessageRole.User,
            $"Deep Fix: install apt package(s): {packageList}.",
            now);
        await conversationStore.SaveMessageAsync(userMessage, cancellationToken);

        var result = await InstallAptPackagesOnLocalHostAsync(normalizedPackageNames, cancellationToken);

        var assistantMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            userMessage.SequenceNumber + 1,
            AiChatMessageRole.Assistant,
            BuildPackageInstallActionMessage(normalizedPackageNames, result),
            DateTimeOffset.UtcNow);
        await conversationStore.SaveMessageAsync(assistantMessage, cancellationToken);

        await conversationStore.SaveThreadAsync(thread with
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.Definition.ProviderType,
            ModelId = selectedModelId,
            UpdatedAtUtc = assistantMessage.CreatedAtUtc
        }, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                userMessage.Id,
                "desktop-assistant.package-install.applied",
                result.IsSuccess
                    ? $"Installed apt package(s): {packageList}."
                    : $"Could not install apt package(s): {packageList}.",
                BuildCommandOutput(result),
                result.IsSuccess ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
                assistantMessage.CreatedAtUtc),
            cancellationToken);

        return await GetWorkspaceAsync(thread.Id, desktopSnapshot, cancellationToken);
    }

    public async Task<DesktopAssistantChatWorkspaceViewModel> RepairAptSourcesAsync(
        Guid? sessionId,
        IReadOnlyDictionary<string, string> arguments,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string? providerKey = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = NormalizeAptSourceListPath(
            arguments.GetValueOrDefault("path")
            ?? arguments.GetValueOrDefault("sourcePath")
            ?? arguments.GetValueOrDefault("ignoredFilePath")
            ?? string.Empty);
        var fileName = Path.GetFileName(sourcePath);
        var threadId = sessionId ?? await CreateSessionAsync(providerKey, modelId, cancellationToken);
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That Desktop Assistant chat session could not be found.");
        if (!IsDesktopAssistantThread(thread))
        {
            throw new InvalidOperationException("That chat session does not belong to Desktop Assistant.");
        }

        var provider = await ResolveProviderAsync(
                string.IsNullOrWhiteSpace(providerKey) ? thread.ProviderKey : providerKey,
                true,
                cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");
        var selectedModelId = ResolveModelId(modelId, string.IsNullOrWhiteSpace(thread.ModelId) ? provider.Settings.DefaultModelId : thread.ModelId);
        var existingMessages = await conversationStore.ListMessagesAsync(thread.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var userMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            GetNextSequenceNumber(existingMessages),
            AiChatMessageRole.User,
            $"Deep Fix: clean apt source warning for {fileName}.",
            now);
        await conversationStore.SaveMessageAsync(userMessage, cancellationToken);

        var result = await RepairAptSourcesOnLocalHostAsync(sourcePath, cancellationToken);

        var assistantMessage = new AiChatMessage(
            Guid.NewGuid(),
            thread.Id,
            userMessage.SequenceNumber + 1,
            AiChatMessageRole.Assistant,
            BuildAptSourceRepairActionMessage(sourcePath, result),
            DateTimeOffset.UtcNow);
        await conversationStore.SaveMessageAsync(assistantMessage, cancellationToken);

        await conversationStore.SaveThreadAsync(thread with
        {
            ProviderKey = provider.ProviderKey,
            ProviderType = provider.Definition.ProviderType,
            ModelId = selectedModelId,
            UpdatedAtUtc = assistantMessage.CreatedAtUtc
        }, cancellationToken);
        await auditService.RecordAsync(
            new AiAuditEntry(
                Guid.NewGuid(),
                thread.Id,
                userMessage.Id,
                "desktop-assistant.apt-source-repair.applied",
                result.IsSuccess
                    ? $"Cleaned apt source warning for {fileName}."
                    : $"Could not clean apt source warning for {fileName}.",
                BuildCommandOutput(result),
                result.IsSuccess ? AiExecutionOutcome.Succeeded : AiExecutionOutcome.Failed,
                assistantMessage.CreatedAtUtc),
            cancellationToken);

        return await GetWorkspaceAsync(thread.Id, desktopSnapshot, cancellationToken);
    }

    private async Task<DesktopSessionBrokerSnapshot> RefreshDesktopEvidenceAsync(
        DesktopSessionBrokerSnapshot desktopSnapshot,
        CancellationToken cancellationToken)
    {
        var session = desktopSnapshot.BestAvailableSession;
        if (session is null)
        {
            return desktopSnapshot;
        }

        try
        {
            return await desktopSessionBroker.RefreshEvidenceAsync(
                session.ConnectionId,
                DesktopEvidenceRefreshTimeout,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return desktopSessionBroker.GetSnapshot();
        }
    }

    private static DesktopAssistantRequestKind ClassifyRequest(string userText)
    {
        var normalized = MultiWhitespacePattern().Replace(userText.Trim().ToLowerInvariant(), " ");
        if (LooksLikeDirectFixRequest(normalized) ||
            MutatingRequestPattern().IsMatch(normalized))
        {
            return DesktopAssistantRequestKind.NeedsApprovalAction;
        }

        if (ReadOnlyQuestionPattern().IsMatch(normalized) ||
            userText.TrimEnd().EndsWith("?", StringComparison.Ordinal))
        {
            return DesktopAssistantRequestKind.ReadOnlyQuestion;
        }

        return DesktopAssistantRequestKind.AdviceOnly;
    }

    private static bool ViolatesReadOnlyResponseContract(string assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            return false;
        }

        return ReadOnlyDeferralPattern().IsMatch(assistantText);
    }

    private async Task<string> ExecuteProviderTextAsync(
        IAiProvider provider,
        AiChatThread providerThread,
        IReadOnlyList<AiChatMessage> existingMessages,
        string prompt,
        CancellationToken cancellationToken)
    {
        var result = await provider.ExecuteTurnAsync(
            new AiProviderTurnRequest(
                providerThread,
                existingMessages,
                [],
                [new AiProviderMessageInputItem(AiChatMessageRole.User, prompt)],
                [],
                provider.Settings.StreamingEnabled,
                InternetResearchAllowed: false),
            cancellationToken: cancellationToken);

        return string.Join(
                Environment.NewLine + Environment.NewLine,
                result.AssistantOutputs
                    .Select(output => output.Content?.Trim())
                    .Where(static content => !string.IsNullOrWhiteSpace(content)))
            .Trim();
    }

    private async Task<IReadOnlyList<DesktopAssistantChatSessionViewModel>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var threads = await conversationStore.ListThreadsAsync(cancellationToken);
        var messages = await conversationStore.ListMessagesAsync(cancellationToken: cancellationToken);
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var providerLabels = configuredProviders.ToDictionary(
            provider => provider.ProviderKey,
            provider => provider.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        var messageCounts = messages
            .GroupBy(message => message.ThreadId)
            .ToDictionary(group => group.Key, group => group.Count(message => message.Role != AiChatMessageRole.System));

        return threads
            .Where(IsDesktopAssistantThread)
            .OrderByDescending(thread => thread.UpdatedAtUtc)
            .Select(thread => new DesktopAssistantChatSessionViewModel(
                thread.Id,
                thread.Title,
                thread.ProviderKey,
                providerLabels.GetValueOrDefault(thread.ProviderKey, thread.ProviderKey),
                thread.ModelId,
                messageCounts.GetValueOrDefault(thread.Id),
                thread.UpdatedAtUtc))
            .ToArray();
    }

    private async Task<IAiProvider?> ResolveProviderAsync(
        string? providerKey,
        bool requirePreferredProvider,
        CancellationToken cancellationToken)
    {
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var runnableProviders = configuredProviders
            .Where(provider =>
                provider.IsEnabled &&
                providerRegistry.FindDefinition(provider.ProviderType)?.IsRuntimeImplemented != false)
            .ToArray();

        var normalizedProviderKey = NormalizeSelection(providerKey);
        var selectedProvider = !string.IsNullOrWhiteSpace(normalizedProviderKey)
            ? runnableProviders.FirstOrDefault(provider => provider.ProviderKey.Equals(normalizedProviderKey, StringComparison.OrdinalIgnoreCase))
            : null;
        if (selectedProvider is null && requirePreferredProvider && !string.IsNullOrWhiteSpace(normalizedProviderKey))
        {
            throw new InvalidOperationException("The selected AI provider is not enabled or runnable.");
        }

        selectedProvider ??= runnableProviders.FirstOrDefault(provider => provider.IsDefault)
            ?? runnableProviders.FirstOrDefault();

        return selectedProvider is null
            ? null
            : await providerRegistry.GetProviderAsync(selectedProvider.ProviderKey, cancellationToken);
    }

    private static Guid? ResolveActiveSessionId(
        Guid? requestedSessionId,
        IReadOnlyList<DesktopAssistantChatSessionViewModel> sessions)
    {
        if (requestedSessionId.HasValue && sessions.Any(session => session.Id == requestedSessionId.Value))
        {
            return requestedSessionId.Value;
        }

        return sessions.FirstOrDefault()?.Id;
    }

    private static bool IsDesktopAssistantThread(AiChatThread thread) =>
        thread.Title.StartsWith(ThreadTitlePrefix, StringComparison.OrdinalIgnoreCase);

    private static string ResolveModelId(string? requestedModelId, string fallbackModelId)
    {
        var normalizedModelId = NormalizeSelection(requestedModelId);
        return string.IsNullOrWhiteSpace(normalizedModelId)
            ? fallbackModelId
            : normalizedModelId;
    }

    private static string NormalizeSelection(string? value) =>
        value?.Trim() ?? string.Empty;

    private static string NormalizeKeyboardLayout(string layout)
    {
        if (!TryNormalizeKeyboardLayout(layout, out var normalized))
        {
            throw new InvalidOperationException("Keyboard layout code is invalid.");
        }

        return normalized;
    }

    private static bool TryNormalizeKeyboardLayout(string layout, out string normalized)
    {
        var token = NormalizeSelection(layout)
            .Trim('*', '`', '\'', '"', '(', ')', '[', ']', '{', '}', '.', ',', ';', ':')
            .ToLowerInvariant();

        normalized = token switch
        {
            "uk" or "british" or "united kingdom" or "united-kingdom" => "gb",
            "usa" or "american" or "united states" or "united-states" => "us",
            _ => token
        };

        return KeyboardLayoutPattern().IsMatch(normalized) &&
               !IsReservedKeyboardLayoutToken(normalized);
    }

    private static bool IsReservedKeyboardLayoutToken(string value) =>
        value is "as" or "be" or "fix" or "is" or "key" or "now" or "set" or "the" or "to" or "use" or "x11" or "xkb";

    private static IReadOnlyList<string> NormalizePackageNames(IReadOnlyList<string> packageNames)
    {
        var normalized = packageNames
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("At least one package name is required.");
        }

        foreach (var packageName in normalized)
        {
            if (!SafePackageNamePattern().IsMatch(packageName))
            {
                throw new InvalidOperationException($"Package name {packageName} is not valid for apt installation.");
            }
        }

        return normalized;
    }

    private static string NormalizeAptSourceListPath(string value) =>
        TryNormalizeAptSourceListPath(value, out var normalized)
            ? normalized
            : throw new InvalidOperationException("Apt source repair path is invalid.");

    private static bool TryNormalizeAptSourceListPath(string value, out string normalized)
    {
        normalized = string.Empty;
        var trimmed = value.Trim().Trim('`', '\'', '"', ',', ';', '.');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(trimmed);
        if (!fullPath.StartsWith(AptSourcesListDirectory, StringComparison.Ordinal) ||
            !SafeAptSourceFileNamePattern().IsMatch(Path.GetFileName(fullPath)))
        {
            return false;
        }

        normalized = fullPath;
        return true;
    }

    private static string BuildKeyboardLayoutActionMessage(
        string layout,
        DesktopSessionActionResult actionResult)
    {
        var builder = new StringBuilder();
        builder.AppendLine(actionResult.Succeeded
            ? $"Done. Keyboard layout is now set to {layout}."
            : $"I tried to set keyboard layout to {layout}, but the desktop helper reported a problem.");

        if (!string.IsNullOrWhiteSpace(actionResult.Summary))
        {
            builder.AppendLine(actionResult.Summary.Trim());
        }

        var keyboardDiagnostics = actionResult.Diagnostics
            .Where(item => item.Key.StartsWith("keyboard.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (keyboardDiagnostics.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Verified keyboard evidence:");
            foreach (var diagnostic in keyboardDiagnostics)
            {
                builder.AppendLine($"- {diagnostic.Key}: {Summarize(diagnostic.Value)}");
            }
        }

        return builder.ToString().Trim();
    }

    private static DesktopAssistantProposedFixViewModel? TryBuildSupportedActionFallback(
        string userText,
        string assistantText,
        IReadOnlyList<AiChatMessage> existingMessages)
    {
        var evidenceText = BuildSupportedFixEvidenceText(userText, assistantText, existingMessages);
        var intentText = LooksLikeFixContinuation(userText) ? evidenceText : userText;

        if (TryResolveKeyboardLayoutIntent(evidenceText, out var layout))
        {
            return new DesktopAssistantProposedFixViewModel(
                DesktopSessionActionKinds.SetKeyboardLayout,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["layout"] = layout
                },
                $"Set keyboard layout: {layout}",
                $"LMS will apply {layout} through the connected desktop helper and verify the desktop keyboard evidence.");
        }

        if (TryResolveIgnoredAptSourceFileFix(evidenceText, out var sourcePath))
        {
            var fileName = Path.GetFileName(sourcePath);
            return new DesktopAssistantProposedFixViewModel(
                DesktopSessionActionKinds.RepairAptSources,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = sourcePath
                },
                "Clean apt source warning",
                $"LMS will move {fileName} out of apt's live source directory and refresh package indexes.");
        }

        if (TryResolvePackageInstallIntent(intentText, out var packageNames))
        {
            var normalizedPackageNames = NormalizePackageNames(packageNames);
            var packageList = string.Join(", ", normalizedPackageNames);
            return new DesktopAssistantProposedFixViewModel(
                DesktopSessionActionKinds.InstallAptPackages,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["packages"] = string.Join(",", normalizedPackageNames)
                },
                $"Install apt package(s): {packageList}",
                $"LMS will install {packageList} on the local LMS machine using the approved package-install runner.");
        }

        return null;
    }

    private static DesktopAssistantProposedFixViewModel? TryRecoverPendingFixFromMessages(
        IReadOnlyList<AiChatMessage> messages)
    {
        var conversation = messages
            .Where(message => message.Role is AiChatMessageRole.User or AiChatMessageRole.Assistant)
            .OrderBy(message => message.SequenceNumber)
            .ToArray();
        var lastAssistantMessage = conversation.LastOrDefault();
        if (lastAssistantMessage is null ||
            lastAssistantMessage.Role != AiChatMessageRole.Assistant ||
            !LooksLikePendingFixAssistantMessage(lastAssistantMessage.Content))
        {
            return null;
        }

        var previousMessages = conversation
            .Take(conversation.Length - 1)
            .ToArray();
        var lastUserMessage = previousMessages.LastOrDefault(message => message.Role == AiChatMessageRole.User);
        return lastUserMessage is null
            ? null
            : TryBuildSupportedActionFallback(lastUserMessage.Content, lastAssistantMessage.Content, previousMessages);
    }

    private static string BuildProposedFixApprovalMessage(DesktopAssistantProposedFixViewModel proposedFix) =>
        $"""
        I can fix this. Do you want LMS to apply it?

        {proposedFix.Title}
        {proposedFix.Description}
        """;

    private static string BuildUnsupportedApprovalFallback() =>
        "I found a likely desktop fix, but LMS could not map it to a safe one-click action. Rephrase with the exact supported action, for example: set keyboard layout to gb.";

    private static string BuildSupportedFixEvidenceText(
        string userText,
        string assistantText,
        IReadOnlyList<AiChatMessage> existingMessages)
    {
        if (!LooksLikeFixContinuation(userText))
        {
            return $"{userText}\n{assistantText}";
        }

        var recentContext = string.Join(
            Environment.NewLine,
            existingMessages
                .Where(message => message.Role is AiChatMessageRole.User or AiChatMessageRole.Assistant)
                .TakeLast(6)
                .Select(message => message.Content));
        return $"{recentContext}\n{assistantText}";
    }

    private static bool LooksLikeFixContinuation(string userText)
    {
        var normalized = MultiWhitespacePattern().Replace(userText.Trim().ToLowerInvariant(), " ");
        return normalized is "fix it" or "so fix it" or "yes" or "y" or "do it" or "go ahead" or "apply it" or "approve it" ||
               normalized.Contains("fix it", StringComparison.Ordinal) ||
               normalized.Contains("go ahead", StringComparison.Ordinal) ||
               normalized.Contains("do it", StringComparison.Ordinal) ||
               normalized.Contains("apply it", StringComparison.Ordinal);
    }

    private static bool LooksLikePendingFixAssistantMessage(string assistantText)
    {
        var normalized = MultiWhitespacePattern().Replace(assistantText.Trim().ToLowerInvariant(), " ");
        return normalized.Contains("do you want lms to apply it", StringComparison.Ordinal) ||
               normalized.Contains("do you want lms to fix", StringComparison.Ordinal) ||
               normalized.Contains("i can fix this", StringComparison.Ordinal);
    }

    private static bool LooksLikeDirectFixRequest(string userText)
    {
        var normalized = MultiWhitespacePattern().Replace(userText.Trim().ToLowerInvariant(), " ");
        return LooksLikeFixContinuation(userText) ||
               LooksLikeIgnoredAptSourceWarning(userText) ||
               normalized.Contains("deep fix", StringComparison.Ordinal) ||
               normalized.Contains(" fix ", StringComparison.Ordinal) ||
               normalized.StartsWith("fix ", StringComparison.Ordinal) ||
               normalized.Contains(" install ", StringComparison.Ordinal) ||
               normalized.StartsWith("install ", StringComparison.Ordinal) ||
               normalized.Contains(" set ", StringComparison.Ordinal) ||
               normalized.StartsWith("set ", StringComparison.Ordinal) ||
               normalized.Contains(" make ", StringComparison.Ordinal) ||
               normalized.StartsWith("make ", StringComparison.Ordinal) ||
               normalized.Contains(" change ", StringComparison.Ordinal) ||
               normalized.StartsWith("change ", StringComparison.Ordinal) ||
               normalized.Contains(" clean ", StringComparison.Ordinal) ||
               normalized.StartsWith("clean ", StringComparison.Ordinal);
    }

    private static bool TryResolveKeyboardLayoutIntent(string userText, out string layout)
    {
        layout = string.Empty;
        if (!KeyboardIntentPattern().IsMatch(userText))
        {
            return false;
        }

        foreach (Match targetMatch in KeyboardTargetLayoutIntentPattern().Matches(userText))
        {
            if (TryNormalizeKeyboardLayout(targetMatch.Groups["layout"].Value, out layout))
            {
                return true;
            }
        }

        var match = KeyboardLayoutIntentPattern().Match(userText);
        if (match.Success && TryNormalizeKeyboardLayout(match.Groups["layout"].Value, out layout))
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveIgnoredAptSourceFileFix(string text, out string sourcePath)
    {
        sourcePath = string.Empty;
        if (!LooksLikeIgnoredAptSourceWarning(text))
        {
            return false;
        }

        var fullPathMatch = AptSourceListPathPattern().Match(text);
        if (fullPathMatch.Success && TryNormalizeAptSourceListPath(fullPathMatch.Groups["path"].Value, out sourcePath))
        {
            return true;
        }

        var warningMatch = IgnoredAptSourceFileWarningPattern().Match(text);
        if (!warningMatch.Success)
        {
            return false;
        }

        var fileName = warningMatch.Groups["file"].Value.Trim();
        if (!SafeAptSourceFileNamePattern().IsMatch(fileName))
        {
            return false;
        }

        return TryNormalizeAptSourceListPath($"{AptSourcesListDirectory}{fileName}", out sourcePath);
    }

    private static bool LooksLikeIgnoredAptSourceWarning(string text) =>
        (text.Contains("Ignoring file", StringComparison.OrdinalIgnoreCase) &&
         text.Contains("invalid filename extension", StringComparison.OrdinalIgnoreCase) &&
         text.Contains("/etc/apt/sources.list.d", StringComparison.OrdinalIgnoreCase)) ||
        (text.Contains(".disabled-by-lms-", StringComparison.OrdinalIgnoreCase) &&
         text.Contains("/etc/apt/sources.list.d", StringComparison.OrdinalIgnoreCase));

    private static bool TryResolvePackageInstallIntent(string userText, out IReadOnlyList<string> packageNames)
    {
        packageNames = [];
        var tokens = userText
            .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.Trim('`', '\'', '"', '.', '!', '?', ':', ')', '('))
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (tokens.Length == 0)
        {
            return false;
        }

        var candidates = new List<string>();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!tokens[index].Equals("install", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var candidateIndex = index + 1; candidateIndex < tokens.Length; candidateIndex++)
            {
                var token = tokens[candidateIndex];
                if (IsPackageInstallFiller(token))
                {
                    if (candidates.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (!SafePackageNamePattern().IsMatch(token))
                {
                    break;
                }

                candidates.Add(token);
            }

            break;
        }

        packageNames = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return packageNames.Count > 0;
    }

    private static bool IsPackageInstallFiller(string value) =>
        value.Equals("the", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("a", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("an", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("apt", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("package", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("app", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("application", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("please", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("and", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("for", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("to", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("use", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("using", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("via", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("through", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("with", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("but", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("because", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("after", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("fails", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("failing", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("me", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("this", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("machine", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("desktop", StringComparison.OrdinalIgnoreCase);

    private async Task<CommandExecutionResult> InstallAptPackagesOnLocalHostAsync(
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken)
    {
        var packageArguments = string.Join(' ', packageNames.Select(QuoteShellArgument));
        var command = $"""
sudo -n /bin/bash -s -- {packageArguments} <<'LMS_APT_DEEP_FIX'
{BuildAptDeepFixScript()}
LMS_APT_DEEP_FIX
""";
        return await commandExecutionService.ExecuteAsync(
            AiLocalMachine.CreateManagedHost(),
            command,
            cancellationToken: cancellationToken);
    }

    private async Task<CommandExecutionResult> RepairAptSourcesOnLocalHostAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var command = $"""
sudo -n /bin/bash -s -- {QuoteShellArgument(sourcePath)} <<'LMS_APT_SOURCE_REPAIR'
{BuildAptSourceRepairScript()}
LMS_APT_SOURCE_REPAIR
""";
        return await commandExecutionService.ExecuteAsync(
            AiLocalMachine.CreateManagedHost(),
            command,
            cancellationToken: cancellationToken);
    }

    private static string BuildAptSourceRepairActionMessage(
        string sourcePath,
        CommandExecutionResult result)
    {
        var fileName = Path.GetFileName(sourcePath);
        var builder = new StringBuilder();
        builder.AppendLine(result.IsSuccess
            ? $"Done. Deep Fix cleaned the apt source warning for {fileName}."
            : $"Deep Fix could not clean the apt source warning for {fileName}.");

        var failureSummary = result.IsSuccess ? string.Empty : BuildAptSourceRepairFailureSummary(result);
        if (!string.IsNullOrWhiteSpace(failureSummary))
        {
            builder.AppendLine();
            builder.AppendLine(failureSummary);
        }

        var output = BuildCommandOutput(result, result.IsSuccess ? 900 : 1200);
        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.AppendLine();
            builder.AppendLine(output);
        }

        return builder.ToString().Trim();
    }

    private static string BuildAptSourceRepairFailureSummary(CommandExecutionResult result)
    {
        var output = $"{result.StandardError}\n{result.StandardOutput}";
        if (output.Contains("sudo: a password is required", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("sudo: a terminal is required", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked: LMS does not currently have non-interactive sudo rights for apt source repair.";
        }

        if (output.Contains("Unsafe apt source repair path", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked: the apt source path failed LMS safety checks.";
        }

        return "Deep Fix tried to move the ignored apt source file out of apt's live source directory and refresh package indexes.";
    }

    private static string BuildPackageInstallActionMessage(
        IReadOnlyList<string> packageNames,
        CommandExecutionResult result)
    {
        var packageList = string.Join(", ", packageNames);
        var builder = new StringBuilder();
        builder.AppendLine(result.IsSuccess
            ? $"Done. Deep Fix installed {packageList}."
            : $"Deep Fix could not complete the install for {packageList}.");

        var failureSummary = result.IsSuccess ? string.Empty : BuildPackageInstallFailureSummary(result);
        if (!string.IsNullOrWhiteSpace(failureSummary))
        {
            builder.AppendLine();
            builder.AppendLine(failureSummary);
        }

        var output = result.IsSuccess
            ? BuildCommandOutput(result)
            : BuildCommandOutput(result, maxChars: 1200);
        if (!string.IsNullOrWhiteSpace(output))
        {
            builder.AppendLine();
            builder.AppendLine(output);
        }

        return builder.ToString().Trim();
    }

    private static string BuildCommandOutput(CommandExecutionResult result, int maxChars = 1800)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Exit code: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            builder.AppendLine("stderr:");
            builder.AppendLine(SummarizeCommandStream(result.StandardError, preferTail: true, maxChars));
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            builder.AppendLine("stdout:");
            builder.AppendLine(SummarizeCommandStream(result.StandardOutput, preferTail: !result.IsSuccess, maxChars));
        }

        return builder.ToString().TrimEnd();
    }

    private static string SummarizeCommandStream(string content, bool preferTail, int maxChars)
    {
        var normalized = content.Trim();
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return preferTail
            ? $"...{normalized[^maxChars..]}"
            : $"{normalized[..maxChars]}...";
    }

    private static string BuildPackageInstallFailureSummary(CommandExecutionResult result)
    {
        var output = $"{result.StandardError}\n{result.StandardOutput}";
        if (output.Contains("sudo: a password is required", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("sudo: a terminal is required", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked: LMS does not currently have non-interactive sudo rights for package repair/install.";
        }

        if (output.Contains("Could not get lock", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Unable to acquire the dpkg frontend lock", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Blocked by active package manager lock", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked: another package manager still owns the apt/dpkg lock. Deep Fix waited and stopped safe GUI package frontends, but an unsafe package process was still active.";
        }

        if (output.Contains("Unable to locate package", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked: apt could not find one of the requested packages after refreshing package indexes.";
        }

        if (output.Contains("held broken packages", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("unmet dependencies", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked: apt dependency repair did not resolve the package set.";
        }

        return "Deep Fix inspected apt state, repaired safe package-manager blockers it found, and retried the install.";
    }

    private static string BuildAptSourceRepairScript() =>
        $$"""
        set -u

        source_dir="{{AptSourcesListDirectory.TrimEnd('/')}}"
        disabled_dir="{{AptDisabledSourcesDirectory}}"
        path="${1:-}"

        log() {
          printf '[lms-apt-source-repair] %s\n' "$*"
        }

        case "$path" in
          "$source_dir"/*) ;;
          *)
            log "Unsafe apt source repair path: $path"
            exit 64
            ;;
        esac

        name="$(basename -- "$path")"
        case "$name" in
          ""|"."|".."|*[!A-Za-z0-9._+-]*)
            log "Unsafe apt source repair filename: $name"
            exit 64
            ;;
        esac

        mkdir -p "$disabled_dir"
        if [[ ! -e "$path" ]]; then
          log "Already clean: $path is not in apt's live source directory."
          apt-get update
          exit $?
        fi

        target="$disabled_dir/$name"
        if [[ -e "$target" ]]; then
          target="$disabled_dir/$name.$(date +%Y%m%d%H%M%S)"
        fi

        mv -- "$path" "$target"
        log "moved ignored apt source file to $target"
        apt-get update
        """;

    private static string BuildAptDeepFixScript() =>
        """
        set -u

        export DEBIAN_FRONTEND=noninteractive
        packages=("$@")

        log() {
          printf '[lms-package-deep-fix] %s\n' "$*"
        }

        lock_holders() {
          local lock
          for lock in /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock /var/cache/apt/archives/lock /var/lib/apt/lists/lock; do
            [[ -e "$lock" ]] || continue
            fuser "$lock" 2>/dev/null || true
          done | tr ' ' '\n' | sed '/^$/d' | sort -un
        }

        process_name() {
          ps -p "$1" -o comm= 2>/dev/null | tr -d '[:space:]'
        }

        process_command() {
          ps -p "$1" -o args= 2>/dev/null | sed 's/^[[:space:]]*//'
        }

        is_safe_gui_package_frontend() {
          case "$1" in
            synaptic|synaptic-pkexec|update-manager|gnome-software|plasma-discover|software-center|packagekitd) return 0 ;;
            *) return 1 ;;
          esac
        }

        print_lock_holders() {
          local pid name command
          for pid in "$@"; do
            name="$(process_name "$pid")"
            command="$(process_command "$pid")"
            log "lock holder pid=$pid name=${name:-unknown} command=${command:-unknown}"
          done
        }

        wait_or_clear_apt_locks() {
          local waited=0
          local pid name
          local -a holders

          while (( waited < 20 )); do
            mapfile -t holders < <(lock_holders)
            (( ${#holders[@]} == 0 )) && return 0
            log "apt/dpkg is locked; waiting for the active package manager to finish."
            print_lock_holders "${holders[@]}"
            sleep 2
            waited=$((waited + 2))
          done

          mapfile -t holders < <(lock_holders)
          (( ${#holders[@]} == 0 )) && return 0

          for pid in "${holders[@]}"; do
            name="$(process_name "$pid")"
            if is_safe_gui_package_frontend "$name"; then
              log "stopping GUI package frontend pid=$pid name=$name so Deep Fix can continue."
              kill -TERM "$pid" 2>/dev/null || true
            fi
          done

          sleep 5
          mapfile -t holders < <(lock_holders)
          (( ${#holders[@]} == 0 )) && return 0

          for pid in "${holders[@]}"; do
            name="$(process_name "$pid")"
            if is_safe_gui_package_frontend "$name"; then
              log "forcing stopped GUI package frontend pid=$pid name=$name."
              kill -KILL "$pid" 2>/dev/null || true
            fi
          done

          sleep 2
          mapfile -t holders < <(lock_holders)
          if (( ${#holders[@]} > 0 )); then
            log "Blocked by active package manager lock:"
            print_lock_holders "${holders[@]}"
            return 75
          fi

          return 0
        }

        repair_duplicate_sources() {
          local output="$1"
          local first second disable keep disabled_dir backup
          local repaired=1
          disabled_dir="/etc/apt/lms-disabled-sources"

          while IFS='|' read -r first second; do
            [[ -n "$first" && -n "$second" ]] || continue
            disable=""
            keep=""
            if [[ "$first" == /etc/apt/sources.list.d/*.list && "$second" == /etc/apt/sources.list.d/*.sources ]]; then
              disable="$first"
              keep="$second"
            elif [[ "$second" == /etc/apt/sources.list.d/*.list && "$first" == /etc/apt/sources.list.d/*.sources ]]; then
              disable="$second"
              keep="$first"
            fi

            [[ -n "$disable" && -f "$disable" ]] || continue
            mkdir -p "$disabled_dir"
            backup="$disabled_dir/$(basename "$disable").disabled-by-lms-$(date +%Y%m%d%H%M%S)"
            log "disabling duplicate apt source $disable; keeping $keep."
            mv "$disable" "$backup"
            repaired=0
          done < <(printf '%s\n' "$output" | sed -nE 's#.* configured multiple times in ([^:]+):[0-9]+ and ([^:]+):[0-9]+.*#\1|\2#p' | sort -u)

          return "$repaired"
        }

        clean_invalid_lms_disabled_sources() {
          local path name disabled_dir target repaired=1
          disabled_dir="/etc/apt/lms-disabled-sources"

          for path in /etc/apt/sources.list.d/*.disabled-by-lms-*; do
            [[ -e "$path" ]] || continue
            name="$(basename "$path")"
            case "$name" in
              ""|"."|".."|*[!A-Za-z0-9._+-]*) continue ;;
            esac

            mkdir -p "$disabled_dir"
            target="$disabled_dir/$name"
            if [[ -e "$target" ]]; then
              target="$disabled_dir/$name.$(date +%Y%m%d%H%M%S)"
            fi

            log "moving ignored LMS-disabled apt source out of live source directory: $path"
            mv "$path" "$target"
            repaired=0
          done

          return "$repaired"
        }

        apt_update_with_repairs() {
          local temp_dir status combined
          temp_dir="$(mktemp -d)"

          clean_invalid_lms_disabled_sources || true

          log "refreshing apt package indexes."
          apt-get update >"$temp_dir/update.out" 2>"$temp_dir/update.err"
          status=$?

          combined="$(cat "$temp_dir/update.out" "$temp_dir/update.err")"
          if printf '%s\n' "$combined" | grep -q 'configured multiple times in '; then
            if repair_duplicate_sources "$combined"; then
              log "duplicate apt source entries repaired; refreshing package indexes again."
              apt-get update
              status=$?
              rm -rf "$temp_dir"
              return "$status"
            fi
          fi

          cat "$temp_dir/update.out"
          cat "$temp_dir/update.err" >&2
          rm -rf "$temp_dir"
          return "$status"
        }

        repair_dpkg_state() {
          if dpkg --audit | grep -q .; then
            log "dpkg reports interrupted package work; configuring pending packages."
            dpkg --configure -a
          fi

          log "checking apt dependency state."
          apt-get -f install -y
        }

        if (( ${#packages[@]} == 0 )); then
          log "no packages were requested."
          exit 64
        fi

        log "Deep Fix requested package install: ${packages[*]}"
        wait_or_clear_apt_locks || exit $?
        apt_update_with_repairs || exit $?
        wait_or_clear_apt_locks || exit $?
        repair_dpkg_state || true

        log "installing package(s): ${packages[*]}"
        if apt-get install -y -- "${packages[@]}"; then
          log "package install completed."
          exit 0
        fi

        log "install failed; repairing package state and retrying once."
        wait_or_clear_apt_locks || exit $?
        dpkg --configure -a || true
        apt-get -f install -y || true
        apt-get install -y -- "${packages[@]}"
        """;

    private static string QuoteShellArgument(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return value.Any(static character => char.IsWhiteSpace(character) || character is '\'' or '"' or '\\' or '$' or '`' or '!' or '*' or '?' or '[' or ']' or '(' or ')' or '{' or '}' or ';' or '&' or '|' or '<' or '>' or '~')
            ? $"'{value.Replace("'", "'\"'\"'")}'"
            : value;
    }

    private static int GetNextSequenceNumber(IReadOnlyList<AiChatMessage> messages) =>
        messages.Count == 0 ? 1 : messages.Max(message => message.SequenceNumber) + 1;

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Enter a message for Desktop Assistant.");
        }

        var trimmed = message.Trim();
        return trimmed.Length <= MaxPromptChars ? trimmed : $"{trimmed[..MaxPromptChars]}...";
    }

    private static string BuildPrompt(
        string userText,
        DesktopAssistantRequestKind requestKind,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string conversationRecap)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are Linux Made Sane Desktop Assistant, a GUI-session agent for the signed-in Linux desktop.");
        builder.AppendLine($"LMS request classification: {requestKind}.");
        builder.AppendLine("The user expects an LMS product experience, not a command-line tutorial. Use the current graphical-session facts and LMS-collected evidence below.");
        builder.AppendLine("Be direct and concise. Answer the user's question first, then give the next LMS action.");
        builder.AppendLine("If the evidence already answers the question, answer from the evidence instead of asking for more checks.");
        builder.AppendLine("Use the broad read-only desktop evidence below to answer process, RAM, window, input, audio, Bluetooth, network, and session questions directly.");
        builder.AppendLine("LMS refreshes read-only desktop evidence before this answer. Read-only checks do not need user approval. Do not answer 'I can run the check' when the user asks to see read-only state; answer from current LMS evidence.");
        builder.AppendLine("For ReadOnlyQuestion, do not offer to run, collect, refresh, check, scan, inspect, or list anything later. LMS has already done the read-only refresh. Say what the current evidence shows, including 'none found' or 'service inactive' when that is what the evidence says.");
        builder.AppendLine("For NeedsApprovalAction, do not claim the change is done unless an LMS action result is present. Ask for approval only for the specific changing action.");
        builder.AppendLine("For AdviceOnly, explain briefly and name a sensible LMS action only if one is actually needed.");
        builder.AppendLine("If the user asks for Deep Fix, behave like Terminal Deep Fix: keep moving toward the fix, ask for approval only when a desktop-changing action is needed, and avoid long diagnosis essays.");
        builder.AppendLine("Do not ask the user to paste command output back into chat. If fresh evidence is needed, name the single LMS check or future helper action needed.");
        builder.AppendLine("If the current evidence is not enough and internet research is needed, say that clearly instead of inventing certainty.");
        builder.AppendLine("Do not claim you changed the desktop unless the prompt includes an explicit LMS desktop action result.");
        builder.AppendLine("Do not suggest arbitrary sudo or shell-heavy workflows as the main answer. Prefer LMS actions, Desktop Options, setup flows, and helper-backed diagnostics.");
        builder.AppendLine("When an available Desktop Assistant action fits the user's goal, say 'I can fix this' and keep the response short; LMS will show the Yes/No approval card separately.");
        builder.AppendLine();
        builder.AppendLine("Current desktop session:");
        builder.AppendLine(BuildDesktopContext(desktopSnapshot));

        if (!string.IsNullOrWhiteSpace(conversationRecap))
        {
            builder.AppendLine();
            builder.AppendLine("Recent Desktop Assistant conversation:");
            builder.AppendLine(conversationRecap);
        }

        builder.AppendLine();
        builder.AppendLine("User message:");
        builder.AppendLine(userText);
        return builder.ToString().Trim();
    }

    private static string BuildReadOnlyContractRetryPrompt(
        string userText,
        DesktopSessionBrokerSnapshot desktopSnapshot,
        string rejectedAssistantText)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The previous Desktop Assistant answer violated the LMS product contract.");
        builder.AppendLine("The user asked for read-only desktop state. LMS already refreshed evidence before asking you.");
        builder.AppendLine("Do not say you can run, check, collect, inspect, scan, list, refresh, or ask for approval.");
        builder.AppendLine("Answer now from the evidence. If the evidence has no matching entries, say that directly and cite the relevant status evidence.");
        builder.AppendLine();
        builder.AppendLine("Rejected answer:");
        builder.AppendLine(rejectedAssistantText);
        builder.AppendLine();
        builder.AppendLine("Current desktop session evidence:");
        builder.AppendLine(BuildDesktopContext(desktopSnapshot));
        builder.AppendLine();
        builder.AppendLine("User message:");
        builder.AppendLine(userText);
        return builder.ToString().Trim();
    }

    private static string BuildReadOnlyGuardFallback(DesktopSessionBrokerSnapshot desktopSnapshot)
    {
        var session = desktopSnapshot.BestAvailableSession;
        if (session is null)
        {
            return "Desktop Assistant is not connected to a ready GUI session.";
        }

        var report = session.CapabilityReport;
        var builder = new StringBuilder();
        builder.AppendLine("I refreshed the desktop evidence, but the AI response did not meet the LMS read-only answer contract.");
        builder.AppendLine($"Current session: {report.DisplayServer} desktop for {report.UserName}; helper last seen {session.LastSeenAtUtc.ToLocalTime():g}.");
        if (report.ReadOnlyDiagnostics.Count > 0)
        {
            builder.AppendLine($"Evidence available: {string.Join(", ", report.ReadOnlyDiagnostics.Keys.OrderBy(key => key, StringComparer.Ordinal).Take(18))}.");
        }
        else
        {
            builder.AppendLine("No read-only helper diagnostics are currently available.");
        }

        return builder.ToString().Trim();
    }

    private static string BuildDesktopContext(DesktopSessionBrokerSnapshot desktopSnapshot)
    {
        var session = desktopSnapshot.BestAvailableSession;
        if (session is null)
        {
            return "No ready desktop helper session is connected.";
        }

        var report = session.CapabilityReport;
        var lines = new List<string>
        {
            $"- User: {report.UserName} ({report.UserId?.ToString() ?? "unknown uid"})",
            $"- Machine: {report.MachineName}",
            $"- Display server: {report.DisplayServer}",
            $"- DISPLAY: {report.Display ?? "not set"}",
            $"- WAYLAND_DISPLAY: {report.WaylandDisplay ?? "not set"}",
            $"- Desktop session: {report.DesktopSession ?? report.CurrentDesktop ?? "unknown"}",
            $"- Session type: {report.SessionType ?? "unknown"}",
            $"- Session bus available: {(report.HasSessionBus ? "yes" : "no")}",
            $"- Can launch GUI apps: {(report.CanLaunchGuiApps ? "yes" : "no")}",
            $"- Available diagnostic tools: {FormatList(report.AvailableTools)}",
            "- LMS desktop surfaces: Assistant chat for GUI issues; Desktop Options for mode/XRDP changes with snapshots; Setup only for helper repair.",
            $"- Approved desktop actions available now: {DesktopSessionActionKinds.SetKeyboardLayout}, {DesktopSessionActionKinds.InstallAptPackages}, {DesktopSessionActionKinds.RepairAptSources}."
        };

        if (report.Warnings.Count > 0)
        {
            lines.Add($"- Warnings: {string.Join("; ", report.Warnings)}");
        }

        lines.Add("");
        lines.Add("Desktop process evidence:");
        lines.Add(BuildDesktopProcessContext(report));

        if (report.ReadOnlyDiagnostics.Count > 0)
        {
            lines.Add("");
            lines.Add("Read-only helper diagnostics:");
            foreach (var diagnostic in report.ReadOnlyDiagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                lines.Add($"[{diagnostic.Key}]");
                lines.Add(SummarizeDiagnosticOutput(diagnostic.Value));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDesktopProcessContext(DesktopSessionCapabilityReport report)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return "Process evidence is only available from LMS on Linux systems with /proc.";
        }

        var processes = EnumerateLinuxProcesses()
            .ToArray();
        var totalMemoryBytes = TryReadTotalSystemMemoryBytes();
        var builder = new StringBuilder();
        if (totalMemoryBytes is > 0)
        {
            builder.AppendLine($"System RAM total from /proc/meminfo: {FormatMemory(totalMemoryBytes.Value)}.");
        }

        if (report.UserId is null)
        {
            builder.AppendLine("Desktop user id is unknown, so LMS cannot confidently filter /proc entries to the signed-in desktop user.");
        }
        else
        {
            var desktopProcesses = processes
                .Where(process => process.UserId == report.UserId.Value)
                .OrderByDescending(process => process.ResidentBytes)
                .ThenBy(process => process.Pid)
                .Take(MaxDesktopProcessRows)
                .ToArray();
            if (desktopProcesses.Length == 0)
            {
                builder.AppendLine($"No readable /proc entries were found for desktop uid {report.UserId.Value}.");
            }
            else
            {
                builder.AppendLine($"Top desktop-user processes by resident RAM for uid {report.UserId.Value} (LMS privileged /proc evidence, {desktopProcesses.Length} shown):");
                AppendProcessRows(builder, desktopProcesses, totalMemoryBytes);
            }
        }

        var systemProcesses = processes
            .OrderByDescending(process => process.ResidentBytes)
            .ThenBy(process => process.Pid)
            .Take(MaxSystemProcessRows)
            .ToArray();
        if (systemProcesses.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Top system processes by resident RAM (LMS privileged /proc evidence, {systemProcesses.Length} shown):");
            AppendProcessRows(builder, systemProcesses, totalMemoryBytes);
        }

        return builder.ToString().Trim();
    }

    private static void AppendProcessRows(
        StringBuilder builder,
        IReadOnlyList<DesktopProcessInfo> processes,
        long? totalMemoryBytes)
    {
        foreach (var process in processes)
        {
            var percent = totalMemoryBytes is > 0
                ? $" ({FormatOneDecimal(process.ResidentBytes * 100d / totalMemoryBytes.Value)}% RAM)"
                : string.Empty;
            var state = string.IsNullOrWhiteSpace(process.State) ? "state unknown" : process.State;
            builder.AppendLine($"- {FormatMemory(process.ResidentBytes)}{percent} RSS | pid {process.Pid} | uid {process.UserId} | {state} | {process.CommandName} | {process.CommandLine}");
        }
    }

    private static IEnumerable<DesktopProcessInfo> EnumerateLinuxProcesses()
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var directoryName = Path.GetFileName(directory);
            if (!int.TryParse(directoryName, out var pid))
            {
                continue;
            }

            if (!TryReadProcessStatus(directory, out var processUid, out var residentBytes, out var state))
            {
                continue;
            }

            var commandName = ReadProcessName(directory);
            var commandLine = ReadProcessCommandLine(directory);
            if (string.IsNullOrWhiteSpace(commandName) && string.IsNullOrWhiteSpace(commandLine))
            {
                continue;
            }

            yield return new DesktopProcessInfo(
                pid,
                processUid,
                string.IsNullOrWhiteSpace(commandName) ? "unknown" : commandName,
                SummarizeProcessCommand(commandLine, commandName),
                residentBytes,
                state);
        }
    }

    private static bool TryReadProcessStatus(
        string procDirectory,
        out int userId,
        out long residentBytes,
        out string state)
    {
        userId = 0;
        residentBytes = 0;
        state = string.Empty;
        var hasUserId = false;

        try
        {
            foreach (var line in File.ReadLines(Path.Combine(procDirectory, "status")))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    hasUserId = parts.Length > 1 && int.TryParse(parts[1], out userId);
                    continue;
                }

                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    residentBytes = TryParseMemoryKilobytes(line, out var rssKilobytes)
                        ? rssKilobytes * 1024L
                        : 0;
                    continue;
                }

                if (line.StartsWith("State:", StringComparison.Ordinal))
                {
                    state = line["State:".Length..].Trim();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return hasUserId;
    }

    private static string ReadProcessName(string procDirectory)
    {
        try
        {
            return File.ReadAllText(Path.Combine(procDirectory, "comm")).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string ReadProcessCommandLine(string procDirectory)
    {
        try
        {
            var raw = File.ReadAllText(Path.Combine(procDirectory, "cmdline"));
            return raw.Replace('\0', ' ').Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string SummarizeProcessCommand(string commandLine, string commandName)
    {
        var value = string.IsNullOrWhiteSpace(commandLine) ? commandName : commandLine;
        var normalized = MultiWhitespacePattern().Replace(value.Trim(), " ");
        return normalized.Length <= MaxProcessCommandChars
            ? normalized
            : $"{normalized[..MaxProcessCommandChars]}...";
    }

    private static long? TryReadTotalSystemMemoryBytes()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    continue;
                }

                return TryParseMemoryKilobytes(line, out var kilobytes)
                    ? kilobytes * 1024L
                    : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool TryParseMemoryKilobytes(string line, out long kilobytes)
    {
        kilobytes = 0;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out kilobytes);
    }

    private static string FormatMemory(long bytes) =>
        $"{FormatOneDecimal(bytes / 1024d / 1024d)} MiB";

    private static string FormatOneDecimal(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string BuildStatusSummary(DesktopSessionBrokerSnapshot desktopSnapshot)
    {
        var session = desktopSnapshot.BestAvailableSession;
        if (session is null)
        {
            var count = desktopSnapshot.Sessions.Count;
            return count == 0
                ? "Desktop agent is still attaching to the graphical session."
                : $"{count} desktop agent report(s), waiting for a GUI-ready session.";
        }

        var report = session.CapabilityReport;
        return $"{report.DisplayServer} desktop for {report.UserName}; last seen {session.LastSeenAtUtc.ToLocalTime():g}.";
    }

    private static string BuildConversationRecap(IReadOnlyList<AiChatMessage> messages)
    {
        var lines = messages
            .Where(message => message.Role is AiChatMessageRole.User or AiChatMessageRole.Assistant)
            .TakeLast(MaxHistoryEntries)
            .Select(message =>
            {
                var speaker = message.Role == AiChatMessageRole.Assistant ? "Assistant" : "User";
                return $"{speaker}: {Summarize(message.Content)}";
            })
            .ToArray();
        var recap = string.Join(Environment.NewLine, lines);
        return recap.Length <= MaxHistoryRecapChars
            ? recap
            : $"...{recap[^MaxHistoryRecapChars..]}";
    }

    private static string Summarize(string content)
    {
        var normalized = MultiWhitespacePattern().Replace(content.Trim(), " ");
        return normalized.Length <= MaxHistoryEntryChars
            ? normalized
            : $"{normalized[..MaxHistoryEntryChars]}...";
    }

    private static string FormatList(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    private static string SummarizeDiagnosticOutput(string content)
    {
        var normalized = content.Trim();
        return normalized.Length <= 900
            ? normalized
            : $"{normalized[..900]}...";
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiWhitespacePattern();

    [GeneratedRegex("^[a-z]{2,3}([_+-][a-z0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyboardLayoutPattern();

    [GeneratedRegex("^[A-Za-z0-9.+:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePackageNamePattern();

    [GeneratedRegex("^[A-Za-z0-9._+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAptSourceFileNamePattern();

    [GeneratedRegex("\\b(keyboard|keymap|xkb|xkbmap)\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyboardIntentPattern();

    [GeneratedRegex("\\b(?:keyboard(?:\\s+layout)?|keymap|xkb(?:map|layout)?)\\b[^.\\n]{0,160}?\\b(?:to|as|use|using|apply)\\b\\s+(?:the\\s+)?(?:desktop\\s+)?(?:cleanly\\s+)?[\\*`'\"(\\[]*(?<layout>uk|gb|us|usa|british|american|[a-z]{2,3}(?:[_+-][a-z0-9]+)?)(?=$|\\s|[\\]})`'\".,;:!?])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyboardTargetLayoutIntentPattern();

    [GeneratedRegex("\\b(?:keyboard(?:\\s+layout)?|keymap|xkb(?:map|layout)?)\\b(?:\\s+(?:to|as|is|set|use|make|be)|\\s*[:=])?\\s+['`\"]?(?<layout>[a-z]{2,3}(?:[_+-][a-z0-9]+)?)(?=$|\\s|[\\]})`'\".,;:!?])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyboardLayoutIntentPattern();

    [GeneratedRegex("(?<path>/etc/apt/sources\\.list\\.d/[A-Za-z0-9._+-]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AptSourceListPathPattern();

    [GeneratedRegex("Ignoring file ['`\"]?(?<file>[^'`\"\\s]+)['`\"]? in directory ['`\"]?/etc/apt/sources\\.list\\.d/?['`\"]?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IgnoredAptSourceFileWarningPattern();

    [GeneratedRegex("^\\s*(?:please\\s+)?(?:(?:can|could|would)\\s+you\\s+)?(?:deep\\s+fix|fix|repair|install|remove|uninstall|purge|restart|start|stop|enable|disable|reload|set|change|make|apply|update|upgrade|clean|delete|kill|configure|write|create|add)\\b|\\b(?:i\\s+)?(?:want|need)\\s+(?:you\\s+to\\s+)?(?:deep\\s+fix|fix|repair|install|remove|uninstall|purge|restart|start|stop|enable|disable|reload|set|change|make|apply|update|upgrade|clean|delete|kill|configure|write|create|add)\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MutatingRequestPattern();

    [GeneratedRegex("^\\s*(?:can\\s+you\\s+)?(?:show|list|what|which|who|where|when|how\\s+many|how\\s+much|tell\\s+me|give\\s+me|display|check|find|status|are|is|do|does)\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReadOnlyQuestionPattern();

    [GeneratedRegex("\\b(?:i\\s+can(?:not|'?t)?|i\\s+could|i\\s+would|lms\\s+can|next\\s+lms\\s+action\\s*:\\s*i\\s+can|please\\s+approve|approve\\s+(?:a\\s+)?(?:check|diagnostic|scan))\\b.{0,120}\\b(?:run|check|collect|refresh|inspect|scan|list|query|gather|look\\s+up)\\b|\\b(?:need|needs)\\s+(?:to\\s+)?(?:run|check|collect|refresh|inspect|scan|list|query|gather)\\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReadOnlyDeferralPattern();

    private sealed record DesktopProcessInfo(
        int Pid,
        int UserId,
        string CommandName,
        string CommandLine,
        long ResidentBytes,
        string State);

    private enum DesktopAssistantRequestKind
    {
        ReadOnlyQuestion,
        NeedsApprovalAction,
        AdviceOnly
    }
}
