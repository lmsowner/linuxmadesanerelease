// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
