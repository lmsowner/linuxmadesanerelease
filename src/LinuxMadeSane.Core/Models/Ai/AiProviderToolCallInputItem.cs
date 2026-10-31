// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderToolCallInputItem(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson) : AiProviderInputItem;
