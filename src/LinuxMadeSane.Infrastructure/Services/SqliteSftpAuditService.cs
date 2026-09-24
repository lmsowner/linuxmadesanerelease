// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.SftpServer;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteSftpAuditService(ISftpServerStore store) : ISftpAuditService
{
    public Task RecordAsync(SftpAuditEntry entry, CancellationToken cancellationToken = default) =>
        store.SaveAuditEntryAsync(entry, cancellationToken);

    public Task<IReadOnlyList<SftpAuditEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAuditEntriesAsync(cancellationToken);

    public Task<IReadOnlyList<SftpAuditEntry>> ListForUserAsync(string userName, CancellationToken cancellationToken = default) =>
        store.ListAuditEntriesAsync(userName, cancellationToken);
}
