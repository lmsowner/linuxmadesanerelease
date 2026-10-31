// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts.Updates;

namespace LinuxMadeSane.Application.Interfaces;

public interface IHostUpdateScheduleStore
{
    Task<HostUpdateScheduleSettings> GetAsync(CancellationToken cancellationToken = default);

    Task<HostUpdateScheduleSettings> SaveAsync(
        HostUpdateScheduleSettings settings,
        CancellationToken cancellationToken = default);
}
