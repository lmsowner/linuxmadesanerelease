// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IAiChatRunQueue
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
    void Cancel(Guid runId);
}
