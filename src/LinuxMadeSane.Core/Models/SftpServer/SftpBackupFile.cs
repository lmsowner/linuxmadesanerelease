// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpBackupFile(
    string SourcePath,
    string BackupPath,
    bool ExistedBeforeSnapshot);
