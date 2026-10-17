// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
