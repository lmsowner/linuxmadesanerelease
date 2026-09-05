// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.Monitoring;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalSystemMonitorService
{
    Task<LocalSystemMonitorSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}
