// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class RemoteLmsAiEngineAiProvider(
    string providerKey,
    AiProviderDefinition definition,
    AiProviderSettings settings,
    IReadOnlyList<AiModelDefinition> models,
    IRemoteLmsAiEngineGateway gateway) : IAiProvider
{
    public string ProviderKey => providerKey;
    public AiProviderDefinition Definition => definition;
    public AiProviderSettings Settings => settings;
    public IReadOnlyList<AiModelDefinition> Models => models;

    public Task<AiProviderTurnResult> ExecuteTurnAsync(
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Thread.ProviderType != AiProviderType.RemoteLmsAiEngine)
        {
            throw new InvalidOperationException("The remote LMS AI Engine adapter can only execute Remote LMS AI Engine chat threads.");
        }

        return gateway.ExecuteAsync(settings, request, textProgress, cancellationToken);
    }
}
