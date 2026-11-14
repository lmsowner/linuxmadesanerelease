// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Monitoring;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalDriveUsageService
{
    Task<LocalDriveUsageSnapshot> EstimateAsync(
        string path, TimeSpan budget, CancellationToken cancellationToken = default);

    Task<LocalDriveUsageSnapshot> ScanAsync(
        string path,
        CancellationToken cancellationToken = default);
}
