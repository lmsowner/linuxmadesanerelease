// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record PackageAction(
    PackageActionKind Action,
    string PackageName,
    string Reason,
    bool IsDestructive,
    string PlannedCommand);
