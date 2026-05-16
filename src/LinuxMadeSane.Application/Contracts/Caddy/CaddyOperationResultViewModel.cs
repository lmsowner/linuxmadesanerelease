// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Models.RdpOptimizer;

namespace LinuxMadeSane.Application.Contracts.Caddy;

public sealed record CaddyOperationResultViewModel(
    bool Success,
    string Summary,
    IReadOnlyList<OperationLogEntry> Logs);
