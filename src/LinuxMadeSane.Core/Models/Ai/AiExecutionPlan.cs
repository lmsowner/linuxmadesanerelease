// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiExecutionPlan(
    Guid Id,
    Guid ThreadId,
    Guid? MessageId,
    string Summary,
    AiExecutionOutcome Outcome,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AiProposedAction> Actions);
