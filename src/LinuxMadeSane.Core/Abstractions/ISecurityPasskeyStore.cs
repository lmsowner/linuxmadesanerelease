// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISecurityPasskeyStore
{
    Task<IReadOnlyList<SecurityPasskeyCredential>> ListByUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<SecurityPasskeyCredential?> GetByCredentialIdAsync(string credentialId, CancellationToken cancellationToken = default);
    Task<bool> CredentialIdExistsAsync(string credentialId, CancellationToken cancellationToken = default);
    Task SaveAsync(SecurityPasskeyCredential credential, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
