// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Interfaces;

public interface IPublicDnsPropagationService
{
    Task<bool> WaitUntilResolvableAsync(
        string hostname,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
