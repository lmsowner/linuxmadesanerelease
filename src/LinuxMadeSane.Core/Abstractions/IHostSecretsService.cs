// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Abstractions;

public interface IHostSecretsService
{
    // Secret references are safe to persist in host records; secret values are not.
    Task<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken = default);

    // Implementations should return an opaque reference after encrypting or delegating storage.
    Task<string> StoreSecretAsync(string secretValue, CancellationToken cancellationToken = default);
}
