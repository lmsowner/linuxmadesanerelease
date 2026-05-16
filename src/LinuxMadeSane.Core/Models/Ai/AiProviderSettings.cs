// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiProviderSettings(
    string ProviderKey,
    AiProviderType ProviderType,
    string DisplayName,
    bool IsEnabled,
    bool IsDefault,
    string BaseUrl,
    string DefaultModelId,
    bool StreamingEnabled,
    bool ToolUseEnabled,
    string Notes,
    string MetadataJson,
    string ApiKeySecretReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
