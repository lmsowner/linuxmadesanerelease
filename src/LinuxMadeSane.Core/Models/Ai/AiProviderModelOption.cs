// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderModelOption(
    AiProviderType ProviderType,
    string ModelId,
    string DisplayName,
    string Description,
    bool SupportsToolInvocation,
    bool IsRecommendedDefault);
