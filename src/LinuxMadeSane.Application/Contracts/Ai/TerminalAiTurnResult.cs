// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record TerminalAiTurnResult(
    string ProviderKey,
    string ProviderLabel,
    string ModelId,
    string ProviderConversationReference,
    string ProviderResponseId,
    AiPromptSanitizationSummary Sanitization,
    TerminalAiConversationEntry UserEntry,
    TerminalAiConversationEntry AssistantEntry);
