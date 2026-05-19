// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
