// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ILocalInstanceIdentityStore
{
    Task<LocalInstanceIdentity?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LocalInstanceIdentity identity, CancellationToken cancellationToken = default);
}
