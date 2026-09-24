// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts.Updates;

namespace LinuxMadeSane.Application.Interfaces;

public interface IHostUpdateScheduleStore
{
    Task<HostUpdateScheduleSettings> GetAsync(CancellationToken cancellationToken = default);

    Task<HostUpdateScheduleSettings> SaveAsync(
        HostUpdateScheduleSettings settings,
        CancellationToken cancellationToken = default);
}
