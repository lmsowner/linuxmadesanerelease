// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiChatOrchestrationService
{
    Task QueueUserTurnAsync(
        AiChatThread thread,
        AiChatMessage userMessage,
        CancellationToken cancellationToken = default);

    Task ProcessRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task RequestCancellationAsync(Guid runId, CancellationToken cancellationToken = default);
}
