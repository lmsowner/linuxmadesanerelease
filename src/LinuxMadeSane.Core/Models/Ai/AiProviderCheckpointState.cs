// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
