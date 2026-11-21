// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.RdpOptimizer;

public sealed class RdpOptimizationRequest
{
    public RdpOptimizationProfile Profile { get; set; } = RdpOptimizationProfile.Default;

    public bool BindNewXrdpToLoopback { get; set; }

    public bool ConfigureRemoteDesktop { get; set; }

    public bool ConfigureDesktop { get; set; }
    public bool RepairDesktop { get; set; }

    public string LocalUserName { get; set; } = "";

    public IReadOnlyList<string> DesktopUserNames { get; set; } = Array.Empty<string>();

    public bool InspectOnly { get; set; }

    public bool InstallXrdpIfMissing { get; set; } = true;

    public bool InstallXfceIfMissing { get; set; } = true;

    public bool DisableGnomeServicesOnly { get; set; }

    public bool DisableGnomeAutostarts { get; set; }

    public bool CreateSnapshotBeforeChanges { get; set; } = true;

    public bool DryRun { get; set; }

    public bool RestoreRemovedPackages { get; set; } = true;

    public Guid? SnapshotIdToRestore { get; set; }

    public IReadOnlyList<string> SelectedGnomePackagesToRemove { get; set; } = Array.Empty<string>();
}
