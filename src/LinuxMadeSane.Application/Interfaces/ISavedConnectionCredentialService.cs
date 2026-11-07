// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Application.Contracts;
namespace LinuxMadeSane.Application.Interfaces;

public interface ISavedConnectionCredentialService
{
    Task<IReadOnlyList<SavedConnectionCredentialSummary>> ListAsync(Guid userId, CancellationToken token = default);
    Task<SavedConnectionCredential?> ResolveAsync(Guid userId, Guid id, CancellationToken token = default);
    Task<SavedConnectionCredentialSummary> SaveAsync(Guid userId, SavedConnectionCredentialEditor editor, CancellationToken token = default);
    Task<IReadOnlyList<SavedCredentialAudit>> AuditAsync(Guid userId, CancellationToken token = default);
    Task DeleteAsync(Guid userId, Guid id, CancellationToken token = default);
}
