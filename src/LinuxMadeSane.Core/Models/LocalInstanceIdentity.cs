// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
