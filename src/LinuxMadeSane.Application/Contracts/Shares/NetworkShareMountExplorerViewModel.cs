// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record NetworkShareMountExplorerViewModel(
    ShareToolingStatusViewModel Tooling,
    NetworkShareMachineDiscoveryResult Discovery,
    IReadOnlyList<CurrentSystemMount> CurrentMounts,
    IReadOnlyList<ManagedRemoteShareMount> ManagedMounts);
