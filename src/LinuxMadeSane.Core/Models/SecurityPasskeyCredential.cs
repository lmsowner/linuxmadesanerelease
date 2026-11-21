// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record SecurityPasskeyCredential(
    Guid Id,
    Guid UserId,
    string CredentialId,
    string PublicKey,
    string UserHandle,
    uint SignatureCounter,
    string FriendlyName,
    bool IsBackedUp,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastUsedAtUtc);
