// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record PackageAction(
    PackageActionKind Action,
    string PackageName,
    string Reason,
    bool IsDestructive,
    string PlannedCommand);
