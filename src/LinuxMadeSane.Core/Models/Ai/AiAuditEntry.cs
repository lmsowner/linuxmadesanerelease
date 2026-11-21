// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
