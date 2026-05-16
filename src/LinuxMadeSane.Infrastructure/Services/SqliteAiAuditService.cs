// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.Ai;

namespace LinuxMadeSane.Infrastructure.Services;

public sealed class SqliteAiAuditService(IAiConversationStore conversationStore) : IAiAuditService
{
    public Task RecordAsync(AiAuditEntry entry, CancellationToken cancellationToken = default) =>
        conversationStore.SaveAuditEntryAsync(entry, cancellationToken);

    public Task<IReadOnlyList<AiAuditEntry>> ListAsync(Guid threadId, CancellationToken cancellationToken = default) =>
        conversationStore.ListAuditEntriesAsync(threadId, cancellationToken);
}
