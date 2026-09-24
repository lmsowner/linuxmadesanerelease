// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
