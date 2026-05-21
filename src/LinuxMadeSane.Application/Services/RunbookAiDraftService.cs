// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts;
using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

public sealed class RunbookAiDraftService(
    IAiProviderRegistry providerRegistry,
    IAiPromptSanitizer promptSanitizer) : IRunbookAiDraftService
{
    public async Task<RunbookAiDraftResult> DraftAsync(
        RunbookAiDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Editor);
        ArgumentNullException.ThrowIfNull(request.Hosts);

        if (string.IsNullOrWhiteSpace(request.UserPrompt))
        {
            throw new InvalidOperationException("Enter what you want the runbook helper to change.");
        }

        var provider = await ResolveProviderAsync(request.ProviderKey, cancellationToken)
            ?? throw new InvalidOperationException("No enabled runnable AI provider is configured.");

        var includeSecretValues = promptSanitizer.IsTrustedLocalProvider(provider.Settings.ProviderType);
        var builtPrompt = RunbookAiHelper.BuildPrompt(
            request.Editor,
            request.Hosts,
            request.UserPrompt,
            includeSecretValues);
        var secretValues = RunbookAiHelper.CollectSecretValues(request.Editor.Parameters);
        var sanitization = promptSanitizer.Sanitize(
            builtPrompt,
            provider.Settings.ProviderType,
            secretValues);
        var messageHistory = BuildMessageHistory(request, provider.Settings.ProviderType, secretValues);
        var now = DateTimeOffset.UtcNow;
        var threadId = messageHistory.FirstOrDefault()?.ThreadId ?? Guid.NewGuid();
        var thread = new AiChatThread(
            threadId,
            BuildThreadTitle(request.Editor),
            provider.ProviderKey,
            provider.Definition.ProviderType,
            string.IsNullOrWhiteSpace(request.ModelId)
                ? provider.Settings.DefaultModelId
                : request.ModelId.Trim(),
            AiTrustProfile.CreatePreset(AiTrustLevel.Guided),
            string.Empty,
            string.Empty,
            now,
            now);
        var attachedServers = ResolveAttachedServers(thread.Id, request.Editor, request.Hosts, now);
        AiProviderInputItem[] inputItems =
        [
            new AiProviderMessageInputItem(AiChatMessageRole.User, sanitization.Content)
        ];

        var result = await provider.ExecuteTurnAsync(
            new AiProviderTurnRequest(
                thread,
                messageHistory,
                attachedServers,
                inputItems,
                Array.Empty<AiToolDefinition>(),
                false,
                false),
            cancellationToken: cancellationToken);

        var assistantText = string.Join(
            Environment.NewLine + Environment.NewLine,
            result.AssistantOutputs
                .Select(output => output.Content?.Trim())
                .Where(static content => !string.IsNullOrWhiteSpace(content)));

        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new InvalidOperationException("The AI provider returned no assistant text.");
        }

        var scriptDraft = RunbookAiHelper.TryExtractScriptDraft(assistantText, out var script)
            ? script
            : RunbookAiHelper.TryExtractCommandDraft(assistantText, out var command)
                ? RunbookExecutionCommandBuilder.NormalizeStoredScript(command)
                : null;

        return new RunbookAiDraftResult(
            provider.ProviderKey,
            string.IsNullOrWhiteSpace(provider.Settings.DisplayName)
                ? provider.Definition.DisplayName
                : provider.Settings.DisplayName,
            string.IsNullOrWhiteSpace(result.ModelId) ? thread.ModelId : result.ModelId,
            assistantText,
            scriptDraft,
            sanitization.Summary);
    }

    private IReadOnlyList<AiChatMessage> BuildMessageHistory(
        RunbookAiDraftRequest request,
        AiProviderType providerType,
        IReadOnlyList<string> secretValues)
    {
        if (request.MessageHistory is null || request.MessageHistory.Count == 0)
        {
            return [];
        }

        return request.MessageHistory
            .OrderBy(message => message.SequenceNumber)
            .Select(message => message with
            {
                Content = promptSanitizer.Sanitize(message.Content, providerType, secretValues).Content
            })
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .ToArray();
    }

    private async Task<IAiProvider?> ResolveProviderAsync(
        string providerKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            return await providerRegistry.GetProviderAsync(providerKey.Trim(), cancellationToken);
        }

        var configuredProviders = await providerRegistry.ListConfiguredProvidersAsync(cancellationToken);
        var runtimeReadyProviders = configuredProviders
            .Where(provider =>
                provider.IsEnabled &&
                providerRegistry.FindDefinition(provider.ProviderType)?.IsRuntimeImplemented != false)
            .ToArray();
        var selectedProvider = runtimeReadyProviders.FirstOrDefault(provider => provider.IsDefault)
            ?? runtimeReadyProviders.FirstOrDefault();

        return selectedProvider is null
            ? null
            : await providerRegistry.GetProviderAsync(selectedProvider.ProviderKey, cancellationToken);
    }

    private static IReadOnlyList<AiAttachedServer> ResolveAttachedServers(
        Guid threadId,
        RunbookEditor editor,
        IReadOnlyList<ManagedHost> hosts,
        DateTimeOffset now)
    {
        var selectedHostIds = editor.IsTemplate
            ? editor.HostId == Guid.Empty ? [] : [editor.HostId]
            : editor.SelectedHostIds.Count > 0
                ? editor.SelectedHostIds
                : editor.HostId == Guid.Empty
                    ? []
                    : [editor.HostId];
        var selectedHostIdSet = selectedHostIds.ToHashSet();

        return hosts
            .Where(host => selectedHostIdSet.Contains(host.Id))
            .OrderBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .Select(host => new AiAttachedServer(
                Guid.NewGuid(),
                threadId,
                host.Id,
                host.Name,
                host.Hostname,
                host.Environment,
                now))
            .ToArray();
    }

    private static string BuildThreadTitle(RunbookEditor editor)
    {
        var name = string.IsNullOrWhiteSpace(editor.Name)
            ? "Untitled runbook"
            : editor.Name.Trim();
        return $"Runbook helper · {name}";
    }
}
