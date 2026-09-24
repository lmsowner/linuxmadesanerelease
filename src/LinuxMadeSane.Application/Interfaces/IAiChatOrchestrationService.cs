// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
