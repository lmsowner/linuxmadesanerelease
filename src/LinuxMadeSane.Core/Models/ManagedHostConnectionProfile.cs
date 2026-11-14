// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record ManagedHostConnectionProfile(
    string Username,
    Guid? SecretHandle,
    bool PreferStoredCredentials,
    bool UseSshTransport = false,
    bool UseSudo = false);

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
