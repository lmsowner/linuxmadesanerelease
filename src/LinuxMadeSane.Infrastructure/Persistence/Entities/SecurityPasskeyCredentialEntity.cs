// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SecurityPasskeyCredentialEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string CredentialId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string UserHandle { get; set; } = string.Empty;
    public uint SignatureCounter { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsBackedUp { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }

    public SecurityUserEntity? User { get; set; }
}
