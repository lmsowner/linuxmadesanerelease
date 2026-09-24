// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiChatRun(
    Guid Id,
    Guid ThreadId,
    Guid MessageId,
    AiChatRunStatus Status,
    AiChatRunStep Step,
    string StatusSummary,
    Guid? ExecutionPlanId,
    int ProviderAttemptCount,
    string CurrentProviderResponseId,
    string PendingAssistantOutputsJson,
    string PendingToolCallsJson,
    string LastError,
    bool CancellationRequested,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    public bool IsTerminal =>
        Status is AiChatRunStatus.Completed or AiChatRunStatus.Failed or AiChatRunStatus.Cancelled;
}
