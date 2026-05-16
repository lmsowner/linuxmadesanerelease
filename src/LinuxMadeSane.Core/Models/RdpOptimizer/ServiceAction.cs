// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record ServiceAction(
    ServiceActionKind Action,
    string ServiceName,
    string Reason,
    bool IsDestructive,
    string PlannedCommand);
