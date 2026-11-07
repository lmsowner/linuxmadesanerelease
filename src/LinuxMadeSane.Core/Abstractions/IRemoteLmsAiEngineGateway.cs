// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;
using LinuxMadeSane.Core.Models.LocalAi;

namespace LinuxMadeSane.Core.Abstractions;

public interface IRemoteLmsAiEngineGateway
{
    Task<IReadOnlyList<RemoteLmsAiEngineDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<AiProviderTurnResult> ExecuteAsync(
        AiProviderSettings settings,
        AiProviderTurnRequest request,
        IProgress<AiProviderTextDelta>? textProgress = null,
        CancellationToken cancellationToken = default);
    Task SyncSharedEngineAsync(
        LocalAiEngineStatus status,
        CancellationToken cancellationToken = default);
}
