// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILinuxCommandRunner
{
    Task<LinuxCommandResult> RunAsync(
        LinuxCommandRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default);
}
