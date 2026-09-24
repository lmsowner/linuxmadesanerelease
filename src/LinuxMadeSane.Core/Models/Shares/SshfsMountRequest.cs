// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record SshfsMountRequest(
    Guid HostId,
    string RemotePath,
    string LocalMountPath,
    bool PersistOnServer);
