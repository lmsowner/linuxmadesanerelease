// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record TerminalAiConversationEntry(
    AiChatMessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc,
    string SuggestedCommand = "");
