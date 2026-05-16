// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Core.Abstractions;

public interface ISftpAuditService
{
    Task RecordAsync(SftpAuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SftpAuditEntry>> ListForUserAsync(string userName, CancellationToken cancellationToken = default);
}
