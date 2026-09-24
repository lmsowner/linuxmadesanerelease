// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiProviderModelDiscoveryService
{
    Task<IReadOnlyList<AiProviderModelOption>> DiscoverAsync(
        AiProviderSettings settings,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default);
}
