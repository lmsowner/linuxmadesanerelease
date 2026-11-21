// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
