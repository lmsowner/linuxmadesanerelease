// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpBackupSnapshot(
    Guid Id,
    string Summary,
    IReadOnlyList<SftpBackupFile> Files,
    DateTimeOffset CreatedAtUtc,
    bool RollbackAvailable,
    string StorageDirectory);
