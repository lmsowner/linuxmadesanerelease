// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiProviderSettingsEditorContextViewModel(
    AiProviderSettingsEditor Editor,
    IReadOnlyList<AiProviderDefinition> SupportedProviders,
    IReadOnlyList<AiProviderModelOption> AvailableModelOptions,
    IReadOnlyList<AiConfiguredProviderViewModel> ExistingProviders,
    bool ProviderExists);
