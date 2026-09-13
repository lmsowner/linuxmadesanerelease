// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IMediaLibraryScanQueue
{
    ValueTask EnqueueScanAsync(Guid? rootId, CancellationToken cancellationToken = default);
}
