// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISecretStore
{
    Task<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default);
    Task<SecretReferenceMetadata?> GetMetadataAsync(string secretReference, CancellationToken cancellationToken = default);
    Task<string> StoreSecretAsync(string secretValue, string purpose, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default);
}
