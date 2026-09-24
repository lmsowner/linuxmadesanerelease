// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class DisabledRemoteLmsAiEngineGateway : IRemoteLmsAiEngineGateway
{
    public Task<IReadOnlyList<RemoteLmsAiEngineDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteLmsAiEngineDescriptor>>([]);

    public Task<AiProviderTurnResult> ExecuteAsync(
        AiProviderSettings settings,
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The LMS Connect client plugin is not installed in this build.");

    public Task SyncSharedEngineAsync(LocalAiEngineStatus status, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
