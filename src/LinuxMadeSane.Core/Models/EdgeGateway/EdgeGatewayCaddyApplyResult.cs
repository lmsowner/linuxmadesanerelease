// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Models.EdgeGateway;

public sealed record EdgeGatewayCaddyApplyResult(
    bool Success,
    string Summary,
    string GeneratedConfigPath,
    IReadOnlyList<OperationLogEntry> Logs);
