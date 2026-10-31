// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
    Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Host shutdown is unavailable."));
}
