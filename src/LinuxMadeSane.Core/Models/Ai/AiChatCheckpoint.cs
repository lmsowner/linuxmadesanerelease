// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatCheckpoint(
    Guid Id,
    Guid ThreadId,
    Guid? MessageId,
    string Label,
    string Summary,
    string StateJson,
    DateTimeOffset CreatedAtUtc);
