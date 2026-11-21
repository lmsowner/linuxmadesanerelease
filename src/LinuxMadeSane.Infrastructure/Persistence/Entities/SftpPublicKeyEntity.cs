// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SftpPublicKeyEntity
{
    public Guid Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string KeyType { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string PublicKeyText { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public SftpManagedUserEntity? User { get; set; }
}
