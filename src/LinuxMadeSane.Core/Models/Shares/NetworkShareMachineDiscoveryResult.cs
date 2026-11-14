// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record NetworkShareMachineDiscoveryResult(
    IReadOnlyList<NetworkShareMachine> Machines,
    string StatusMessage,
    IReadOnlyList<string> Notes,
    NetworkShareDiscoveryScope Scope = NetworkShareDiscoveryScope.Lan,
    bool CanDiscoverTailnet = false);
