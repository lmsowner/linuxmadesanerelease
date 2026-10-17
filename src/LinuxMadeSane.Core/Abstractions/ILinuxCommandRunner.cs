// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILinuxCommandRunner
{
    Task<LinuxCommandResult> RunAsync(
        LinuxCommandRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
