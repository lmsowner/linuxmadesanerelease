// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderTurnResult(
    string ProviderResponseId,
    string? ConversationReference,
    string? ModelId,
    IReadOnlyList<AiProviderAssistantOutput> AssistantOutputs,
    IReadOnlyList<AiProviderToolCallRequest> ToolCalls);
