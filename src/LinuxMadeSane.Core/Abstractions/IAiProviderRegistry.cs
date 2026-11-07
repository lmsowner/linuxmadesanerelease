// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProviderRegistry
{
    IReadOnlyList<AiProviderDefinition> ListSupportedProviders();
    IReadOnlyList<AiProviderModelOption> ListModelCatalog(AiProviderType? providerType = null);
    Task<IReadOnlyList<AiProviderSettings>> ListConfiguredProvidersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiModelDefinition>> ListModelsAsync(string? providerKey = null, CancellationToken cancellationToken = default);
    AiProviderDefinition? FindDefinition(AiProviderType providerType);
    Task<IAiProvider?> GetProviderAsync(string providerKey, CancellationToken cancellationToken = default);
}
