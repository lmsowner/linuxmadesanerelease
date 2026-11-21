// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Contracts;
namespace LinuxMadeSane.Application.Interfaces;
public interface IMailRelayCredentialService
{
    Task<IReadOnlyList<SavedConnectionCredentialSummary>> ListAsync(CancellationToken token = default);
    Task<SavedConnectionCredential> ResolveProviderAsync(Guid id, CancellationToken token = default);
    Task<SavedConnectionCredentialSummary> SaveAsync(ConnectionCredentialKind kind, string name, string host, string username, string password, CancellationToken token = default);
    Task<string> ReadClientPasswordAsync(string host, string username, CancellationToken token = default);
}
