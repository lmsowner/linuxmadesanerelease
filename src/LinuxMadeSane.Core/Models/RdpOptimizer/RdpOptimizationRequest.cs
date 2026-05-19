// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed class RdpOptimizationRequest
{
    public RdpOptimizationProfile Profile { get; set; } = RdpOptimizationProfile.Default;

    public bool InspectOnly { get; set; }

    public bool InstallXrdpIfMissing { get; set; } = true;

    public bool InstallXfceIfMissing { get; set; } = true;

    public bool DisableGnomeServicesOnly { get; set; }

    public bool DisableGnomeAutostarts { get; set; } = true;

    public bool CreateSnapshotBeforeChanges { get; set; } = true;

    public bool DryRun { get; set; }

    public bool RestoreRemovedPackages { get; set; } = true;

    public Guid? SnapshotIdToRestore { get; set; }

    public IReadOnlyList<string> SelectedGnomePackagesToRemove { get; set; } = Array.Empty<string>();
}
