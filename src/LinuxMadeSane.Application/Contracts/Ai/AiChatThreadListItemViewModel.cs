// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiChatThreadListItemViewModel(
    Guid Id,
    string Title,
    string ProviderLabel,
    string ModelId,
    AiTrustLevel TrustLevel,
    int AttachedServerCount,
    int MessageCount,
    int PendingApprovalCount,
    DateTimeOffset UpdatedAtUtc);
