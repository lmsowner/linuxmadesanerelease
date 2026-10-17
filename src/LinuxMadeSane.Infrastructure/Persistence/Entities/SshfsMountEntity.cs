// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SshfsMountEntity
{
    public Guid Id { get; set; }
    public Guid HostId { get; set; }
    public string HostDisplayName { get; set; } = string.Empty;
    public string HostAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string LocalMountPath { get; set; } = string.Empty;
    public string IdentityFilePath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastMountedAtUtc { get; set; }
}
