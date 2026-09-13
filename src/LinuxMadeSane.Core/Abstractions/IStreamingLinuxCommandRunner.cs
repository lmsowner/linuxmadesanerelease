// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
