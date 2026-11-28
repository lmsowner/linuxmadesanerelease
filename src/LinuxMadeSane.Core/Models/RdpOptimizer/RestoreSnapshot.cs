// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed record RestoreSnapshot(
    Guid SnapshotId,
    DateTimeOffset CreatedAt,
    RdpOptimizationProfile ProfileApplied,
    string UbuntuVersion,
    string? DisplayManager,
    IReadOnlyList<PackageState> RelevantPackages,
    IReadOnlyList<ServiceState> RelevantServices,
    DesktopSessionConfiguration SessionConfiguration,
    IReadOnlyList<SessionFileBackup> FileBackups,
    IReadOnlyList<string> RemovedPackages,
    IReadOnlyList<string> Notes);
