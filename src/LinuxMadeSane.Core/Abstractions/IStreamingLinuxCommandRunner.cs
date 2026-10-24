// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface IStreamingLinuxCommandRunner
{
    Task<LinuxCommandResult> RunStreamingAsync(
        LinuxCommandRequest request,
        bool dryRun,
        Action<LinuxCommandOutput> onOutput,
        CancellationToken cancellationToken = default);
}
