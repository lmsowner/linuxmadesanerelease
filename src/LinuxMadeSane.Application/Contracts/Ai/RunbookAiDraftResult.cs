// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record RunbookAiDraftResult(
    string ProviderKey,
    string ProviderDisplayName,
    string ModelId,
    string AssistantText,
    string? ScriptDraft,
    AiPromptSanitizationSummary SanitizationSummary);
