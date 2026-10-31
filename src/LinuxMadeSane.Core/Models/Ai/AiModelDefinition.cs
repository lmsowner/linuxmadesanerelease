// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiModelDefinition(
    string ProviderKey,
    string ModelId,
    string DisplayName,
    string Description,
    int? ContextWindow,
    bool SupportsToolInvocation);
