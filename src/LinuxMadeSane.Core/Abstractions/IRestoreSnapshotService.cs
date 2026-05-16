// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface IRestoreSnapshotService
{
    Task<RestoreSnapshot> CreateSnapshotAsync(
        RdpOptimizationProfile profile,
        DesktopInspectionReport inspection,
        IReadOnlyList<string> filesToBackup,
        CancellationToken cancellationToken = default);

    Task UpdateSnapshotAsync(
        RestoreSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<RestoreSnapshot?> GetSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RestoreSnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken = default);

    Task SaveRunResultAsync(
        RdpOptimizationResult result,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RdpOptimizationResult>> ListRunResultsAsync(CancellationToken cancellationToken = default);
}
