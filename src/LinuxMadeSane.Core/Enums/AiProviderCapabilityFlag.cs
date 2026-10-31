// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

[Flags]
public enum AiProviderCapabilityFlag
{
    None = 0,
    BasicChat = 1 << 0,
    CommandExplanation = 1 << 1,
    LogSummary = 1 << 2,
    FixPlanGeneration = 1 << 3,
    ToolCalling = 1 << 4,
    Streaming = 1 << 5,
    DeepFixRecommended = 1 << 6,
    DeepFixAllowedWithExtraApproval = 1 << 7
}
