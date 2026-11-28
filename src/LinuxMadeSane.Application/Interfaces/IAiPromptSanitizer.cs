// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
