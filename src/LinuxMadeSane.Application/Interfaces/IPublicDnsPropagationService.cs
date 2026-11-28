// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IPublicDnsPropagationService
{
    Task<bool> WaitUntilResolvableAsync(
        string hostname,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
