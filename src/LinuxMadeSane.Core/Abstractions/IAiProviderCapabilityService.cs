// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProviderCapabilityService
{
    Task<LocalAiCapabilityReport> AssessAsync(
        AiProviderSettings settings,
        string modelId,
        CancellationToken cancellationToken = default);
}
