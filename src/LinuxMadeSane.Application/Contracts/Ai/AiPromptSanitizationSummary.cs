// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiPromptSanitizationSummary(
    bool Applied,
    bool TrustedLocalProvider,
    int RedactionCount,
    IReadOnlyList<string> Categories,
    string Message);
