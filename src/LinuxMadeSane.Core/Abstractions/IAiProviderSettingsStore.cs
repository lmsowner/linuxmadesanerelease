// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProviderSettingsStore
{
    Task<IReadOnlyList<AiProviderSettings>> ListAsync(CancellationToken cancellationToken = default);
    Task<AiProviderSettings?> GetAsync(string providerKey, CancellationToken cancellationToken = default);
    Task SaveAsync(AiProviderSettings settings, CancellationToken cancellationToken = default);
}
