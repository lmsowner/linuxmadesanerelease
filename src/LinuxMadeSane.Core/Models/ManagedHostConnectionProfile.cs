// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record ManagedHostConnectionProfile(
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    bool UseSshTransport = false);

public sealed record ManagedHostConnectionSecrets(
    string Password,
    string PrivateKey,
    string PrivateKeyPassphrase)
{
    public static ManagedHostConnectionSecrets Empty { get; } = new(string.Empty, string.Empty, string.Empty);

    public bool HasAnyValue =>
        !string.IsNullOrWhiteSpace(Password) ||
        !string.IsNullOrWhiteSpace(PrivateKey) ||
        !string.IsNullOrWhiteSpace(PrivateKeyPassphrase);
}

public sealed record ManagedHostConnectionValidationResult(
    bool Success,
    string FailureMessage);
