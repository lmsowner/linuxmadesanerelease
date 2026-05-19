// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalInstanceIdentityEntity
{
    public int Id { get; set; } = 1;
    public Guid InstanceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string PrivateKeySecretReference { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PublicKeyFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? RegisteredWithPublicSiteAtUtc { get; set; }
}
