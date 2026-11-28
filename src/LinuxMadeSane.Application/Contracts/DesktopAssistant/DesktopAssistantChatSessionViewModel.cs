// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.DesktopAssistant;

public sealed record DesktopAssistantChatSessionViewModel(
    Guid Id,
    string Title,
    string ProviderKey,
    string ProviderLabel,
    string ModelId,
    int MessageCount,
    DateTimeOffset UpdatedAtUtc);
