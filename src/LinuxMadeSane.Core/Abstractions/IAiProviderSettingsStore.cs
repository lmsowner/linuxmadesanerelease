// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProviderSettingsStore
{
    Task<IReadOnlyList<AiProviderSettings>> ListAsync(CancellationToken cancellationToken = default);
    Task<AiProviderSettings?> GetAsync(string providerKey, CancellationToken cancellationToken = default);
    Task SaveAsync(AiProviderSettings settings, CancellationToken cancellationToken = default);
}
