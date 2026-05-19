// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record ServiceState(
    string Name,
    bool IsEnabled,
    bool IsActive,
    bool IsMasked,
    string UnitFileState,
    string ActiveState,
    string Description);
