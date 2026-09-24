// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.DesktopAssistant;

public sealed record DesktopAssistantChatSessionViewModel(
    Guid Id,
    string Title,
    string ProviderKey,
    string ProviderLabel,
    string ModelId,
    int MessageCount,
    DateTimeOffset UpdatedAtUtc);
