// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record OperationLogEntry(
    DateTimeOffset Timestamp,
    OperationLogLevel Level,
    string Message,
    string? CommandText,
    int? ExitCode,
    string? StandardOutput,
    string? StandardError);
