// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record RunbookAiDraftResult(
    string ProviderKey,
    string ProviderDisplayName,
    string ModelId,
    string AssistantText,
    string? ScriptDraft,
    AiPromptSanitizationSummary SanitizationSummary);
