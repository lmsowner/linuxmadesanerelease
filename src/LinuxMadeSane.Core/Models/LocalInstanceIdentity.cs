// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record LocalInstanceIdentity(
    Guid InstanceId,
    string DisplayName,
    string PrivateKeySecretReference,
    string PublicKey,
    string PublicKeyFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? RegisteredWithPublicSiteAtUtc);
