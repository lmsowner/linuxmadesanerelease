// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpApplyResult(
    bool Success,
    string Summary,
    IReadOnlyList<OperationLogEntry> Logs,
    SftpValidationResult Validation,
    SftpBackupSnapshot? BackupSnapshot,
    bool RollbackApplied,
    IReadOnlyList<string> Warnings);
