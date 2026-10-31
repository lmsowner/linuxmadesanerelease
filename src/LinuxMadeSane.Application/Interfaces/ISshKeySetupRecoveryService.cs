// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Interfaces;

public interface ISshKeySetupRecoveryService
{
    Task<HostConnectionTestResult> RecoverLocalAsync(string userName, GeneratedSshKeyPair key,
        Func<CancellationToken, Task<HostConnectionTestResult>> verify,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default);

    Task<HostConnectionTestResult> RecoverAsync(Guid hostId, GeneratedSshKeyPair key,
        Func<CancellationToken, Task<HostConnectionTestResult>> verify,
        Func<string, Task>? progress = null, CancellationToken cancellationToken = default);
}
