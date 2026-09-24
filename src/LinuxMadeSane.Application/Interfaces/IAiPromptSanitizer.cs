// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Ai;
using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiPromptSanitizer
{
    bool IsTrustedLocalProvider(AiProviderType providerType);

    AiPromptSanitizationResult Sanitize(
        string content,
        AiProviderType providerType,
        IEnumerable<string>? additionalSecrets = null);
}
