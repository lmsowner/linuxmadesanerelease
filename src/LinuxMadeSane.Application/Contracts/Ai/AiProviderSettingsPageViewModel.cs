// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiProviderSettingsPageViewModel(
    IReadOnlyList<AiProviderDefinition> SupportedProviders,
    IReadOnlyList<AiConfiguredProviderViewModel> Providers);
