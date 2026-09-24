// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Monitoring;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalSystemMaintenanceService
{
    Task<string> EndProcessAsync(
        int processId,
        long expectedStartTimeTicks,
        string expectedName,
        CancellationToken cancellationToken = default);

    Task<LocalDiskCleanupResult> CleanupDiskAsync(CancellationToken cancellationToken = default);

    Task RebootAsync(CancellationToken cancellationToken = default);
}
