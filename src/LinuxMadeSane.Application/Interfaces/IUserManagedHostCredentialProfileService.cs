// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts;

namespace LinuxMadeSane.Application.Interfaces;

public interface IUserManagedHostCredentialProfileService
{
    Task<IReadOnlyList<UserManagedHostCredentialProfileSummary>> ListAsync(
        Guid userId,
        Guid managedHostId,
        CancellationToken cancellationToken = default);

    Task<UserManagedHostCredentialProfileCredentials?> ResolveAsync(
        Guid userId,
        Guid managedHostId,
        Guid profileId,
        CancellationToken cancellationToken = default);

    Task<UserManagedHostCredentialProfileSummary> SaveAsync(
        UserManagedHostCredentialProfileEditor editor,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid userId,
        Guid managedHostId,
        Guid profileId,
        CancellationToken cancellationToken = default);
}
