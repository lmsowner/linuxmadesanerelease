// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolInvocation(
    Guid Id,
    Guid ThreadId,
    Guid? MessageId,
    Guid? ExecutionPlanId,
    Guid? ProposedActionId,
    string ToolName,
    string ArgumentsJson,
    AiInvocationStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    AiToolResult? Result);
