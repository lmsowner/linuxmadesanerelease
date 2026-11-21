// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record UserManagedHostCredentialProfile(
    Guid Id,
    Guid UserId,
    Guid ManagedHostId,
    string Name,
    string Username,
    string? PasswordSecretReference,
    string? PrivateKeySecretReference,
    string? PrivateKeyPassphraseSecretReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
