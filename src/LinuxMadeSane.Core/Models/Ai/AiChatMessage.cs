// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatMessage(
    Guid Id,
    Guid ThreadId,
    int SequenceNumber,
    AiChatMessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc);
