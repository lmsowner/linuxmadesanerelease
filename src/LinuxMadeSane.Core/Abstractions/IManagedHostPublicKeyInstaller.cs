// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IManagedHostPublicKeyInstaller
{
    Task<CommandExecutionResult> InstallAsync(ManagedHost host, string publicKey, CancellationToken cancellationToken = default);
}
