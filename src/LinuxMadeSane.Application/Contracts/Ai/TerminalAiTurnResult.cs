// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
