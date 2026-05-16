// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record ManagedSshfsMount(
    Guid Id,
    Guid HostId,
    string HostName,
    string Hostname,
    int Port,
    string UserName,
    string RemotePath,
    string LocalMountPath,
    bool IsMounted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastMountedAtUtc,
    string StatusMessage);
