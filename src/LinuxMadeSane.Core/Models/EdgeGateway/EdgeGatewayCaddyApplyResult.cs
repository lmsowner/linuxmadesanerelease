// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewayCaddyApplyResult(
    bool Success,
    string Summary,
    string GeneratedConfigPath,
    IReadOnlyList<OperationLogEntry> Logs);
