// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Shares;

public static class NetworkMountSelection
{
    public static CurrentSystemMount? FindDisconnectableNetworkMount(
        IEnumerable<CurrentSystemMount> mounts, string localMountPath)
    {
        var path = localMountPath.Trim().TrimEnd('/');
        return mounts.FirstOrDefault(mount => mount.IsNetworkMount &&
            string.Equals(mount.LocalMountPath.TrimEnd('/'), path, StringComparison.Ordinal));
    }

}
