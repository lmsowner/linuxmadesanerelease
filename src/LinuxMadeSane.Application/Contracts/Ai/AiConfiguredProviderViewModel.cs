// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Ai;

public sealed record AiConfiguredProviderViewModel(
    string ProviderKey,
    AiProviderType ProviderType,
    string DisplayName,
    bool IsEnabled,
    bool IsDefault,
    string DefaultModelId,
    bool StreamingEnabled,
    bool ToolUseEnabled,
    bool HasApiKeyConfigured,
    bool RequiresApiKey);
