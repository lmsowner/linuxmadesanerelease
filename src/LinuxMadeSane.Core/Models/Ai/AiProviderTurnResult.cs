// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderTurnResult(
    string ProviderResponseId,
    string? ConversationReference,
    string? ModelId,
    IReadOnlyList<AiProviderAssistantOutput> AssistantOutputs,
    IReadOnlyList<AiProviderToolCallRequest> ToolCalls);
