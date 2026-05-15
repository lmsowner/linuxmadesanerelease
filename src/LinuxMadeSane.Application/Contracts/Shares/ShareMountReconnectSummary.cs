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
