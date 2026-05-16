// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderModelOption(
    AiProviderType ProviderType,
    string ModelId,
    string DisplayName,
    string Description,
    bool SupportsToolInvocation,
    bool IsRecommendedDefault);
