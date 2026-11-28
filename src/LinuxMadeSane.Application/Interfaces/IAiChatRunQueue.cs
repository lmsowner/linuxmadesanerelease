// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiChatRunQueue
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
    void Cancel(Guid runId);
}
