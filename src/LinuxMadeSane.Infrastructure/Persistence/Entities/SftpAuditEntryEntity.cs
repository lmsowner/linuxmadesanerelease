// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SftpAuditEntryEntity
{
    public Guid Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    public string TargetKey { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public bool Success { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? BackupSnapshotId { get; set; }
}
