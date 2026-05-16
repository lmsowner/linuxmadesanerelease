// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISessionConfigurationService
{
    Task<DesktopSessionConfiguration> InspectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionConfigurationChange>> BuildOptimizationChangesAsync(
        DesktopInspectionReport inspection,
        RdpOptimizationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationLogEntry>> ApplyOptimizationChangesAsync(
        IReadOnlyList<SessionConfigurationChange> changes,
        bool disableGnomeAutostarts,
        bool dryRun,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationLogEntry>> RestoreAsync(
        RestoreSnapshot snapshot,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
