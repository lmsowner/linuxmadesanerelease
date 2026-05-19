// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;
using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record SambaSystemActionResult(
    SambaSystemAction Action,
    bool Success,
    string StatusMessage,
    string StatusTone,
    SambaShareSystemCheckResult SystemCheck,
    IReadOnlyList<OperationLogEntry> OperationLogs);
