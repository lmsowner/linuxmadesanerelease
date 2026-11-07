// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
