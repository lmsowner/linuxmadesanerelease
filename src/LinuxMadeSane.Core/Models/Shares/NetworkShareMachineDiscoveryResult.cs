// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record NetworkShareMachineDiscoveryResult(
    IReadOnlyList<NetworkShareMachine> Machines,
    string StatusMessage,
    IReadOnlyList<string> Notes,
    NetworkShareDiscoveryScope Scope = NetworkShareDiscoveryScope.Lan,
    bool CanDiscoverTailnet = false);
