// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiToolResult(
    Guid Id,
    Guid InvocationId,
    AiExecutionOutcome Outcome,
    string Summary,
    string OutputText,
    string ErrorText,
    string PayloadJson,
    int? ExitCode,
    DateTimeOffset CompletedAtUtc);
