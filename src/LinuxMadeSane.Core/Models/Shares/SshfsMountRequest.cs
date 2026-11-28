// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record SshfsMountRequest(
    Guid HostId,
    string RemotePath,
    string LocalMountPath,
    bool PersistOnServer);
