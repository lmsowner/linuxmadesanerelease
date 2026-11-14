// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpBackupService
{
    Task<SftpBackupSnapshot> CreateSnapshotAsync(
        string summary,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(SftpBackupSnapshot snapshot, CancellationToken cancellationToken = default);
}
