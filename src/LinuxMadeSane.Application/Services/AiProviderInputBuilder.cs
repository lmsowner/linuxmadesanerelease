// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

internal static class AiProviderInputBuilder
{
    public static IReadOnlyList<AiProviderInputItem> Build(
        AiProviderDefinition definition,
        AiChatThread thread,
        AiChatMessage userMessage,
        IReadOnlyList<AiChatMessage> messageHistory,
        IReadOnlyList<AiProviderInputItem>? explicitInputs,
        bool isContinuation,
        IReadOnlyList<AiProposedAction> continuationActions,
        Guid runId)
    {
        if (definition.SupportsConversationState)
        {
            return explicitInputs ?? BuildStatefulInitial(thread, userMessage, messageHistory);
        }

        return isContinuation
            ? BuildStatelessContinuation(messageHistory, explicitInputs ?? [], continuationActions, runId)
            : BuildStatelessInitial(messageHistory);
    }

    private static IReadOnlyList<AiProviderInputItem> BuildStatefulInitial(
        AiChatThread thread,
        AiChatMessage userMessage,
        IReadOnlyList<AiChatMessage> messageHistory) =>
        string.IsNullOrWhiteSpace(thread.ProviderStateReference)
            ? messageHistory.Select(MapMessage).ToArray()
            : [new AiProviderMessageInputItem(userMessage.Role, userMessage.Content)];

    private static IReadOnlyList<AiProviderInputItem> BuildStatelessInitial(
        IReadOnlyList<AiChatMessage> messageHistory) =>
        messageHistory.Select(MapMessage).ToArray();

    private static IReadOnlyList<AiProviderInputItem> BuildStatelessContinuation(
        IReadOnlyList<AiChatMessage> messageHistory,
        IReadOnlyList<AiProviderInputItem> explicitInputs,
        IReadOnlyList<AiProposedAction> continuationActions,
        Guid runId)
    {
        var currentToolMessageIds = continuationActions
            .Select(action => CreateToolMessageId(runId, action.Id))
            .ToHashSet();

        var historyInputs = messageHistory
            .Where(message => !(message.Role == AiChatMessageRole.Tool && currentToolMessageIds.Contains(message.Id)))
            .Select(MapMessage);
        var toolCallInputs = continuationActions
            .OrderBy(action => action.SequenceNumber)
            .Select(action => new AiProviderToolCallInputItem(
                action.ProviderToolCallId,
                action.ToolName,
                action.ToolArgumentsJson))
            .Cast<AiProviderInputItem>();

        return historyInputs
            .Concat(toolCallInputs)
            .Concat(explicitInputs)
            .ToArray();
    }

    public static Guid CreateToolMessageId(Guid runId, Guid actionId) =>
        DeterministicGuid.Create($"{runId}:tool-message:{actionId}");

    private static AiProviderMessageInputItem MapMessage(AiChatMessage message) =>
        new(message.Role, message.Content);
}
