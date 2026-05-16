// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public abstract record AiProviderInputItem;

public sealed record AiProviderMessageInputItem(
    AiChatMessageRole Role,
    string Content) : AiProviderInputItem;

public sealed record AiProviderToolOutputInputItem(
    string ToolCallId,
    string ToolName,
    string OutputJson) : AiProviderInputItem;
