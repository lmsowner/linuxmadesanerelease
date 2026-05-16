// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiPromptSanitizationSummary(
    bool Applied,
    bool TrustedLocalProvider,
    int RedactionCount,
    IReadOnlyList<string> Categories,
    string Message);
