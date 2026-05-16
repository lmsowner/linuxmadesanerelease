// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Core.Abstractions;

public interface IAiProvider
{
    string ProviderKey { get; }
    AiProviderDefinition Definition { get; }
    AiProviderSettings Settings { get; }
    IReadOnlyList<AiModelDefinition> Models { get; }
    Task<AiProviderTurnResult> ExecuteTurnAsync(
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default);
}
