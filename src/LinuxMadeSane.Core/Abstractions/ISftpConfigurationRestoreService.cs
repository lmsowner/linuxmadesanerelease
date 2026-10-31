// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Core.Models.SftpServer;
namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpConfigurationRestoreService
{
    Task<SftpApplyResult> RestoreAsync(Guid snapshotId, bool approved, CancellationToken cancellationToken = default);
}
