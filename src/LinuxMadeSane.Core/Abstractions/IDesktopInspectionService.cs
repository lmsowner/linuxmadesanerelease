// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface IDesktopInspectionService
{
    Task<DesktopInspectionReport> InspectAsync(CancellationToken cancellationToken = default);
}
