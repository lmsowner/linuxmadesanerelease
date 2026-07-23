// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface IUserManagedHostCredentialProfileStore
{
    Task<IReadOnlyList<UserManagedHostCredentialProfile>> ListAsync(
        Guid userId,
        Guid managedHostId,
        CancellationToken cancellationToken = default);

    Task<UserManagedHostCredentialProfile?> GetAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    Task<UserManagedHostCredentialProfile?> FindByNameAsync(
        Guid userId,
        Guid managedHostId,
        string name,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        UserManagedHostCredentialProfile profile,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}
