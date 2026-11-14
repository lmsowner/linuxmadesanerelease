// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderCheckpointState(
    string ProviderKey,
    AiProviderType ProviderType,
    string? ConversationReference,
    string? ProviderStateReference,
    string? PreviousProviderStateReference,
    string? ModelId,
    IReadOnlyList<AiProviderToolCallRequest> ToolCalls,
    DateTimeOffset CapturedAtUtc);
