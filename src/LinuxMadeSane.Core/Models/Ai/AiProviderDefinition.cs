// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderDefinition(
    string Key,
    AiProviderType ProviderType,
    string DisplayName,
    string Description,
    bool SupportsToolInvocation,
    bool SupportsApprovals,
    bool IsRuntimeImplemented = true,
    string RuntimeNotes = "",
    bool SupportsConversationState = true,
    bool RequiresApiKey = true);
