// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IMediaLibraryScanQueue
{
    ValueTask EnqueueScanAsync(Guid? rootId, CancellationToken cancellationToken = default);
}
