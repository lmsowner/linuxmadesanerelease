// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiAuditEntry(
    Guid Id,
    Guid ThreadId,
    Guid? MessageId,
    string EventType,
    string Summary,
    string Details,
    AiExecutionOutcome Outcome,
    DateTimeOffset CreatedAtUtc)
{
    public string MetadataJson { get; init; } = string.Empty;
}
