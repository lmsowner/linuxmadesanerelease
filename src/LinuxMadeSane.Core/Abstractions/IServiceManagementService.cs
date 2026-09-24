// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface IServiceManagementService
{
    Task<IReadOnlyList<ServiceState>> InspectAsync(
        IReadOnlyList<string> serviceNames,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationLogEntry>> ApplyActionsAsync(
        IReadOnlyList<ServiceAction> actions,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
