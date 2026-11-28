// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Monitoring;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalSystemMonitorService
{
    Task<LocalSystemMonitorSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
    Task<LocalSystemMonitorSnapshot> CaptureAsync(
        LocalSystemMonitorCaptureOptions options,
        CancellationToken cancellationToken = default);
}
