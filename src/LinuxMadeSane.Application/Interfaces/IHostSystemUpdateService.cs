// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Updates;

namespace LinuxMadeSane.Application.Interfaces;

public interface IHostSystemUpdateService
{
    HostSystemUpdateSnapshot GetSnapshot();

    Task<HostSystemUpdateSnapshot> RefreshAsync(
        bool refreshMetadata,
        CancellationToken cancellationToken = default);

    Task<HostSystemUpdateSnapshot> ApplyPackageUpdatesAsync(
        HostPackageUpdateMode mode,
        bool rebootIfRequired,
        CancellationToken cancellationToken = default);

    Task<HostSystemUpdateSnapshot> StartReleaseUpgradeAsync(
        CancellationToken cancellationToken = default);

    Task<HostUpdateScheduleSettings> GetScheduleAsync(CancellationToken cancellationToken = default);

    Task<HostUpdateScheduleSettings> SaveScheduleAsync(
        HostUpdateScheduleSettings settings,
        CancellationToken cancellationToken = default);

    Task RebootAsync(CancellationToken cancellationToken = default);
}
