// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatCheckpoint(
    Guid Id,
    Guid ThreadId,
    Guid? MessageId,
    string Label,
    string Summary,
    string StateJson,
    DateTimeOffset CreatedAtUtc);
