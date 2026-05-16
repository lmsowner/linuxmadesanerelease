// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpBackupFile(
    string SourcePath,
    string BackupPath,
    bool ExistedBeforeSnapshot);
