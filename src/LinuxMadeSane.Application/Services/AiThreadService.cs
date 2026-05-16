// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Text.Json;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

public sealed class AiThreadService(
    IAiConversationStore conversationStore,
    IManagedHostStore hostStore,
    IAiProviderRegistry providerRegistry,
    IAiAuditService auditService) : IAiThreadService
{
    public async Task<AiOverviewViewModel> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var threads = await conversationStore.ListThreadsAsync(cancellationToken);
        var attachedServers = await conversationStore.ListAttachedServersAsync(cancellationToken: cancellationToken);
        var approvalRequests = await conversationStore.ListApprovalRequestsAsync(cancellationToken: cancellationToken);
        var auditEntries = await conversationStore.ListAuditEntriesAsync(cancellationToken: cancellationToken);
        var checkpoints = await conversationStore.ListCheckpointsAsync(cancellationToken: cancellationToken);
        var supportedProviders = providerRegistry.ListSupportedProviders();
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);

        return new AiOverviewViewModel(
            threads.Count,
            attachedServers.Count,
            approvalRequests.Count(request => request.State == AiApprovalState.Pending),
            auditEntries.Count,
            checkpoints.Count,
            supportedProviders.Count,
            configuredProviders.Count,
            supportedProviders,
            AiProviderViewModelMapper.Map(configuredProviders));
    }

    public async Task<IReadOnlyList<AiChatThreadListItemViewModel>> ListThreadsAsync(CancellationToken cancellationToken = default)
    {
        var threads = await conversationStore.ListThreadsAsync(cancellationToken);
        var attachedServers = await conversationStore.ListAttachedServersAsync(cancellationToken: cancellationToken);
        var messages = await conversationStore.ListMessagesAsync(cancellationToken: cancellationToken);
        var approvalRequests = await conversationStore.ListApprovalRequestsAsync(cancellationToken: cancellationToken);
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);

        var attachedServersByThread = attachedServers
            .GroupBy(server => server.ThreadId)
            .ToDictionary(group => group.Key, group => group.Count());
        var messagesByThread = messages
            .GroupBy(message => message.ThreadId)
            .ToDictionary(group => group.Key, group => group.Count());
        var pendingApprovalsByThread = approvalRequests
            .Where(request => request.State == AiApprovalState.Pending)
            .GroupBy(request => request.ThreadId)
            .ToDictionary(group => group.Key, group => group.Count());

        return threads
            .Select(thread =>
            {
                var configuredProvider = configuredProviders
                    .FirstOrDefault(provider => provider.ProviderKey.Equals(thread.ProviderKey, StringComparison.OrdinalIgnoreCase));

                var providerLabel = configuredProvider?.DisplayName;
                if (string.IsNullOrWhiteSpace(providerLabel))
                {
                    providerLabel = thread.ProviderType == AiProviderType.Unknown
                        ? "Not assigned"
                        : thread.ProviderType.ToString();
                }

                return new AiChatThreadListItemViewModel(
                    thread.Id,
                    thread.Title,
                    providerLabel,
                    string.IsNullOrWhiteSpace(thread.ModelId) ? "Not assigned" : thread.ModelId,
                    thread.TrustProfile.TrustLevel,
                    attachedServersByThread.GetValueOrDefault(thread.Id),
                    messagesByThread.GetValueOrDefault(thread.Id),
                    pendingApprovalsByThread.GetValueOrDefault(thread.Id),
                    thread.UpdatedAtUtc);
            })
            .ToArray();
    }

    public async Task<Guid> CreateThreadAsync(CancellationToken cancellationToken = default)
    {
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var models = await providerRegistry.ListModelsAsync(cancellationToken: cancellationToken);
        var editor = BuildDefaultEditor(configuredProviders, models);
        return await SaveThreadAsync(editor, cancellationToken);
    }

    public async Task<AiChatThreadEditorContextViewModel> GetEditorAsync(Guid? threadId = null, CancellationToken cancellationToken = default)
    {
        var availableServers = await hostStore.ListAsync(cancellationToken);
        var supportedProviders = providerRegistry.ListSupportedProviders();
        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var models = await providerRegistry.ListModelsAsync(cancellationToken: cancellationToken);

        if (!threadId.HasValue)
        {
            return new AiChatThreadEditorContextViewModel(
                BuildDefaultEditor(configuredProviders, models),
                availableServers,
                supportedProviders,
                AiProviderViewModelMapper.Map(configuredProviders),
                models);
        }

        var thread = await conversationStore.GetThreadAsync(threadId.Value, cancellationToken);
        var attachedServers = await conversationStore.ListAttachedServersAsync(threadId.Value, cancellationToken);

        var editor = thread is null
            ? BuildMissingThreadEditor(threadId, configuredProviders, models)
            : MapEditor(thread, attachedServers);

        return new AiChatThreadEditorContextViewModel(
            editor,
            availableServers,
            supportedProviders,
            AiProviderViewModelMapper.Map(configuredProviders),
            models);
    }

    public async Task<Guid> SaveThreadAsync(AiChatThreadEditor editor, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var threadId = editor.Id ?? Guid.NewGuid();
        var existing = editor.Id.HasValue
            ? await conversationStore.GetThreadAsync(threadId, cancellationToken)
            : null;

        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var providerSettings = configuredProviders
            .FirstOrDefault(provider => provider.ProviderKey.Equals(editor.ProviderKey, StringComparison.OrdinalIgnoreCase));
        var configuredModels = string.IsNullOrWhiteSpace(editor.ProviderKey)
            ? []
            : await providerRegistry.ListModelsAsync(editor.ProviderKey, cancellationToken);

        var providerKey = providerSettings?.ProviderKey ?? string.Empty;
        var providerType = providerSettings?.ProviderType ?? AiProviderType.Unknown;
        var modelId = string.IsNullOrWhiteSpace(editor.ModelId)
            ? providerSettings?.DefaultModelId ?? string.Empty
            : editor.ModelId.Trim();

        if (configuredModels.Count > 0 &&
            configuredModels.All(model => !model.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
        {
            modelId = configuredModels.FirstOrDefault()?.ModelId
                ?? providerSettings?.DefaultModelId
                ?? string.Empty;
        }

        var selectedServerIds = editor.AttachedServerIds
            .Distinct()
            .ToArray();

        var availableServers = await hostStore.ListAsync(cancellationToken);
        var attachedServers = availableServers
            .Where(server => selectedServerIds.Contains(server.Id))
            .Select(server => new AiAttachedServer(
                Guid.NewGuid(),
                threadId,
                server.Id,
                server.Name,
                server.Hostname,
                server.Environment,
                now))
            .ToArray();

        if (attachedServers.Length != selectedServerIds.Length)
        {
            throw new InvalidOperationException("One or more selected servers are no longer available.");
        }

        var thread = new AiChatThread(
            threadId,
            NormalizeTitle(editor.Title),
            providerKey,
            providerType,
            modelId,
            new AiTrustProfile(
                editor.TrustLevel,
                editor.AllowReadOnlyTools,
                editor.AllowMutatingTools,
                editor.RequireApprovalForMediumRisk,
                editor.RequireApprovalForHighRisk),
            ShouldResetProviderState(existing, providerKey, modelId)
                ? string.Empty
                : existing?.ProviderConversationReference ?? string.Empty,
            ShouldResetProviderState(existing, providerKey, modelId)
                ? string.Empty
                : existing?.ProviderStateReference ?? string.Empty,
            existing?.CreatedAtUtc ?? now,
            now);

        await conversationStore.SaveThreadAsync(thread, cancellationToken);
        await conversationStore.ReplaceAttachedServersAsync(threadId, attachedServers, cancellationToken);

        var effectiveAttachedServers = AiLocalMachine.GetEffectiveAttachedServers(threadId, attachedServers);

        var checkpoint = new AiChatCheckpoint(
            Guid.NewGuid(),
            threadId,
            null,
            existing is null ? "Thread created" : "Thread updated",
            BuildCheckpointSummary(thread, effectiveAttachedServers),
            BuildCheckpointStateJson(thread, effectiveAttachedServers),
            now);

        await conversationStore.SaveCheckpointAsync(checkpoint, cancellationToken);

        var auditEntry = new AiAuditEntry(
            Guid.NewGuid(),
            threadId,
            null,
                existing is null ? "thread.created" : "thread.updated",
                existing is null ? "AI chat thread created" : "AI chat thread updated",
                BuildAuditDetails(thread, effectiveAttachedServers),
                AiExecutionOutcome.Succeeded,
                now);

        await auditService.RecordAsync(auditEntry, cancellationToken);

        return threadId;
    }

    public async Task UpdateProviderSelectionAsync(
        Guid threadId,
        string providerKey,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var thread = await conversationStore.GetThreadAsync(threadId, cancellationToken)
            ?? throw new InvalidOperationException("That AI chat thread could not be found.");
        var attachedServers = await conversationStore.ListAttachedServersAsync(threadId, cancellationToken);
        var editor = MapEditor(thread, attachedServers);
        editor.ProviderKey = providerKey?.Trim() ?? string.Empty;
        editor.ModelId = modelId?.Trim() ?? string.Empty;
        await SaveThreadAsync(editor, cancellationToken);
    }

    private static AiChatThreadEditor MapEditor(AiChatThread thread, IReadOnlyList<AiAttachedServer> attachedServers) =>
        new()
        {
            Id = thread.Id,
            Title = thread.Title,
            ProviderKey = thread.ProviderKey,
            ModelId = thread.ModelId,
            TrustLevel = thread.TrustProfile.TrustLevel,
            AllowReadOnlyTools = thread.TrustProfile.AllowReadOnlyTools,
            AllowMutatingTools = thread.TrustProfile.AllowMutatingTools,
            RequireApprovalForMediumRisk = thread.TrustProfile.RequireApprovalForMediumRisk,
            RequireApprovalForHighRisk = thread.TrustProfile.RequireApprovalForHighRisk,
            AttachedServerIds = attachedServers.Select(server => server.ManagedHostId).ToList()
        };

    private AiChatThreadEditor BuildDefaultEditor(
        IReadOnlyList<AiProviderSettings> configuredProviders,
        IReadOnlyList<AiModelDefinition> models)
    {
        var runtimeReadyProviders = configuredProviders
            .Where(provider => providerRegistry.FindDefinition(provider.ProviderType)?.IsRuntimeImplemented != false)
            .ToArray();
        var defaultProvider = runtimeReadyProviders.FirstOrDefault(provider => provider.IsDefault && provider.IsEnabled)
            ?? runtimeReadyProviders.FirstOrDefault(provider => provider.IsEnabled)
            ?? configuredProviders.FirstOrDefault(provider => provider.IsDefault && provider.IsEnabled)
            ?? configuredProviders.FirstOrDefault(provider => provider.IsEnabled)
            ?? configuredProviders.FirstOrDefault();
        var defaultModelId = defaultProvider is null
            ? string.Empty
            : models.FirstOrDefault(model => model.ProviderKey.Equals(defaultProvider.ProviderKey, StringComparison.OrdinalIgnoreCase))?.ModelId
              ?? defaultProvider.DefaultModelId;
        var trustProfile = AiTrustProfile.CreatePreset(AiTrustLevel.Guided);

        return new AiChatThreadEditor
        {
            Title = "Untitled AI chat",
            ProviderKey = defaultProvider?.ProviderKey ?? string.Empty,
            ModelId = defaultModelId,
            TrustLevel = trustProfile.TrustLevel,
            AllowReadOnlyTools = trustProfile.AllowReadOnlyTools,
            AllowMutatingTools = trustProfile.AllowMutatingTools,
            RequireApprovalForMediumRisk = trustProfile.RequireApprovalForMediumRisk,
            RequireApprovalForHighRisk = trustProfile.RequireApprovalForHighRisk
        };
    }

    private AiChatThreadEditor BuildMissingThreadEditor(
        Guid? threadId,
        IReadOnlyList<AiProviderSettings> configuredProviders,
        IReadOnlyList<AiModelDefinition> models)
    {
        var editor = BuildDefaultEditor(configuredProviders, models);
        editor.Id = threadId;
        return editor;
    }

    private static string NormalizeTitle(string title)
    {
        var normalized = title.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? $"AI chat {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}"
            : normalized;
    }

    private static bool ShouldResetProviderState(AiChatThread? existing, string providerKey, string modelId)
    {
        if (existing is null)
        {
            return true;
        }

        return !string.Equals(existing.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(existing.ModelId, modelId, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCheckpointSummary(AiChatThread thread, IReadOnlyList<AiAttachedServer> attachedServers)
    {
        var machineSummary = attachedServers.Count == 1 && AiLocalMachine.IsLocalMachine(attachedServers[0].ManagedHostId)
            ? "local machine default"
            : $"{attachedServers.Count} linked machine(s)";

        return $"{thread.Title} | {machineSummary} | trust {thread.TrustProfile.TrustLevel}";
    }

    private static string BuildCheckpointStateJson(AiChatThread thread, IReadOnlyList<AiAttachedServer> attachedServers) =>
        JsonSerializer.Serialize(new
        {
            thread.Title,
            thread.ProviderKey,
            ProviderType = thread.ProviderType.ToString(),
            thread.ModelId,
            thread.ProviderConversationReference,
            thread.ProviderStateReference,
            TrustLevel = thread.TrustProfile.TrustLevel.ToString(),
            thread.TrustProfile.AllowReadOnlyTools,
            thread.TrustProfile.AllowMutatingTools,
            thread.TrustProfile.RequireApprovalForMediumRisk,
            thread.TrustProfile.RequireApprovalForHighRisk,
            AttachedServers = attachedServers.Select(server => new
            {
                server.ManagedHostId,
                server.ServerName,
                server.Hostname,
                server.Environment
            })
        });

    private static string BuildAuditDetails(AiChatThread thread, IReadOnlyList<AiAttachedServer> attachedServers)
    {
        var providerSummary = thread.ProviderType == AiProviderType.Unknown
            ? "No provider configured yet"
            : $"{thread.ProviderType} / {(string.IsNullOrWhiteSpace(thread.ModelId) ? "no model selected" : thread.ModelId)}";

        var serverSummary = attachedServers.Count == 0
            ? "Local machine (default)"
            : attachedServers.Count == 1 && AiLocalMachine.IsLocalMachine(attachedServers[0].ManagedHostId)
                ? "Local machine (default)"
            : string.Join(", ", attachedServers.Select(server => server.ServerName));

        return $"Provider: {providerSummary}. Trust: {thread.TrustProfile.TrustLevel}. Linked machines: {serverSummary}.";
    }
}
