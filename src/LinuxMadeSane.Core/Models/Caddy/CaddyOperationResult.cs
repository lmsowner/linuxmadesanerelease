// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Core.Models.Caddy;

public sealed record CaddyOperationResult(
    bool Success,
    string Summary,
    IReadOnlyList<OperationLogEntry> Logs);
