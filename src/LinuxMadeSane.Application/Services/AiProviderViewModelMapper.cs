// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Services;

internal static class AiProviderViewModelMapper
{
    public static AiConfiguredProviderViewModel Map(AiProviderSettings settings, bool requiresApiKey = true) =>
        new(
            settings.ProviderKey,
            settings.ProviderType,
            settings.DisplayName,
            settings.IsEnabled,
            settings.IsDefault,
            settings.DefaultModelId,
            settings.StreamingEnabled,
            settings.ToolUseEnabled,
            !string.IsNullOrWhiteSpace(settings.ApiKeySecretReference),
            requiresApiKey);

    public static IReadOnlyList<AiConfiguredProviderViewModel> Map(
        IReadOnlyList<AiProviderSettings> settings,
        Func<AiProviderSettings, bool>? requiresApiKeyResolver = null) =>
        settings.Select(item => Map(item, requiresApiKeyResolver?.Invoke(item) ?? true)).ToArray();
}
