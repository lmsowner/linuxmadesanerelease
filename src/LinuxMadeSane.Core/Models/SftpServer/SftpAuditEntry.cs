// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpAuditEntry(
    Guid Id,
    string EventType,
    string TargetType,
    string TargetKey,
    string Summary,
    string Details,
    bool Success,
    DateTimeOffset CreatedAtUtc,
    Guid? BackupSnapshotId);
