// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiProviderModelDiscoveryService
{
    Task<IReadOnlyList<AiProviderModelOption>> DiscoverAsync(
        AiProviderSettings settings,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default);
}
