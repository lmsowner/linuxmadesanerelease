// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Application.Contracts.Sftp;

public sealed record SftpServerWorkspaceViewModel(
    SftpHostConfiguration Overview,
    IReadOnlyList<SftpManagedUser> Users,
    IReadOnlyList<SftpAuditEntry> AuditEntries,
    IReadOnlyList<SftpBackupSnapshot> BackupSnapshots);
