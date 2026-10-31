// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatThread(
    Guid Id,
    string Title,
    string ProviderKey,
    AiProviderType ProviderType,
    string ModelId,
    AiTrustProfile TrustProfile,
    string ProviderConversationReference,
    string ProviderStateReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
