// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
