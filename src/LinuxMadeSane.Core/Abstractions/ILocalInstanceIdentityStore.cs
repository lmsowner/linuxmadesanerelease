// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalInstanceIdentityStore
{
    Task<LocalInstanceIdentity?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LocalInstanceIdentity identity, CancellationToken cancellationToken = default);
}
