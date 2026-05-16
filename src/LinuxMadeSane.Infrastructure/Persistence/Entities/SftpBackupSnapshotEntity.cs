// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SftpBackupSnapshotEntity
{
    public Guid Id { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string FilesJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool RollbackAvailable { get; set; }

    public string StorageDirectory { get; set; } = string.Empty;
}
