// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

// Guardrail: keep provider/model fallback rules here. AI surfaces should not
// each invent their own "default provider" logic because that is how explicit
// operator choices get stomped by a later rerender.
public static class AiProviderSelectionResolver
{
    public static AiProviderSelectionResolution Resolve(
        string? requestedProviderKey,
        string? requestedModelId,
        string? defaultProviderKey,
        string? defaultModelId,
        IReadOnlyList<AiConfiguredProviderViewModel> providers,
        IReadOnlyList<AiModelDefinition> models)
    {
        var providerKey = ResolveProviderKey(
            requestedProviderKey,
            defaultProviderKey,
            providers);
        var selectedProvider = providers.FirstOrDefault(provider =>
            provider.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase));
        var matchingModels = string.IsNullOrWhiteSpace(providerKey)
            ? Array.Empty<AiModelDefinition>()
            : models
                .Where(model => model.ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return new AiProviderSelectionResolution(
            providerKey,
            ResolveModelId(
                Normalize(requestedModelId),
                Normalize(defaultModelId),
                selectedProvider,
                matchingModels),
            selectedProvider?.DisplayName ?? string.Empty);
    }

    private static string ResolveProviderKey(
        string? requestedProviderKey,
        string? defaultProviderKey,
        IReadOnlyList<AiConfiguredProviderViewModel> providers)
    {
        var normalizedRequestedProviderKey = Normalize(requestedProviderKey);
        if (!string.IsNullOrWhiteSpace(normalizedRequestedProviderKey) &&
            providers.Any(provider => provider.ProviderKey.Equals(normalizedRequestedProviderKey, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedRequestedProviderKey;
        }

        var normalizedDefaultProviderKey = Normalize(defaultProviderKey);
        if (!string.IsNullOrWhiteSpace(normalizedDefaultProviderKey) &&
            providers.Any(provider => provider.ProviderKey.Equals(normalizedDefaultProviderKey, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedDefaultProviderKey;
        }

        return providers
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(provider => provider.ProviderKey)
            .FirstOrDefault()
            ?? string.Empty;
    }

    private static string ResolveModelId(
        string requestedModelId,
        string defaultModelId,
        AiConfiguredProviderViewModel? selectedProvider,
        IReadOnlyList<AiModelDefinition> matchingModels)
    {
        if (!string.IsNullOrWhiteSpace(requestedModelId) &&
            matchingModels.Any(model => model.ModelId.Equals(requestedModelId, StringComparison.OrdinalIgnoreCase)))
        {
            return requestedModelId;
        }

        var providerDefaultModelId = Normalize(selectedProvider?.DefaultModelId);
        if (!string.IsNullOrWhiteSpace(providerDefaultModelId) &&
            (matchingModels.Count == 0 ||
             matchingModels.Any(model => model.ModelId.Equals(providerDefaultModelId, StringComparison.OrdinalIgnoreCase))))
        {
            return providerDefaultModelId;
        }

        if (!string.IsNullOrWhiteSpace(defaultModelId) &&
            (matchingModels.Count == 0 ||
             matchingModels.Any(model => model.ModelId.Equals(defaultModelId, StringComparison.OrdinalIgnoreCase))))
        {
            return defaultModelId;
        }

        if (!string.IsNullOrWhiteSpace(requestedModelId) && matchingModels.Count == 0)
        {
            return requestedModelId;
        }

        return matchingModels.FirstOrDefault()?.ModelId ?? string.Empty;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}

public sealed record AiProviderSelectionResolution(
    string ProviderKey,
    string ModelId,
    string ProviderLabel);
