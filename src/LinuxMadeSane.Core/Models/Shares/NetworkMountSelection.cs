// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
