// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

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
