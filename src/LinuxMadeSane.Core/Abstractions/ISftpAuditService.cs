// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpAuditService
{
    Task RecordAsync(SftpAuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListForUserAsync(string userName, CancellationToken cancellationToken = default);
}
