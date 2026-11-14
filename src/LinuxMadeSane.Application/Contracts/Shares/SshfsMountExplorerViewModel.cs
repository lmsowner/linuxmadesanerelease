// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Models.Shares;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record SshfsMountExplorerViewModel(
    SshfsToolingStatusViewModel Tooling,
    IReadOnlyList<SshfsMountHostCandidate> HostCandidates,
    IReadOnlyList<CurrentSystemMount> CurrentMounts,
    IReadOnlyList<ManagedSshfsMount> ManagedMounts);
