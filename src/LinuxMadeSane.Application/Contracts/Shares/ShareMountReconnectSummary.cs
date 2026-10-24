// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record ShareMountReconnectSummary(
    int ReconnectedNetworkMounts,
    int FailedNetworkMounts,
    int ReconnectedSshfsMounts,
    int FailedSshfsMounts)
{
    public int TotalReconnected => ReconnectedNetworkMounts + ReconnectedSshfsMounts;

    public int TotalFailed => FailedNetworkMounts + FailedSshfsMounts;
}
