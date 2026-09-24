// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Monitoring;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalDriveUsageService
{
    Task<LocalDriveUsageSnapshot> ScanAsync(
        string path,
        CancellationToken cancellationToken = default);
}
