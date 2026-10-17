// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface IPackageManagementService
{
    Task<IReadOnlyList<PackageState>> InspectAsync(
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationLogEntry>> ApplyActionsAsync(
        IReadOnlyList<PackageAction> actions,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
