// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SftpManagedUserEntity
{
    public string UserName { get; set; } = string.Empty;

    public int AuthenticationMode { get; set; }

    public bool IsEnabled { get; set; }

    public bool HasPassword { get; set; }

    public string LoginShell { get; set; } = string.Empty;

    public string PrimaryGroup { get; set; } = string.Empty;

    public string SupplementaryGroupsJson { get; set; } = string.Empty;

    public string BasePath { get; set; } = string.Empty;

    public string ChrootPath { get; set; } = string.Empty;

    public string WritablePath { get; set; } = string.Empty;

    public string ChrootOwner { get; set; } = string.Empty;

    public string ChrootGroup { get; set; } = string.Empty;

    public string ChrootMode { get; set; } = string.Empty;

    public string WritableOwner { get; set; } = string.Empty;

    public string WritableGroup { get; set; } = string.Empty;

    public string WritableMode { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? PasswordChangedAtUtc { get; set; }

    public List<SftpPublicKeyEntity> PublicKeys { get; set; } = [];
}
