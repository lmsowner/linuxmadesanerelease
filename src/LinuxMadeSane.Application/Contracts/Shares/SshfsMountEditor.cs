// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed class SshfsMountEditor
{
    [Required]
    public Guid HostId { get; set; }

    [Required]
    public string RemotePath { get; set; } = string.Empty;

    [Required]
    public string LocalMountPath { get; set; } = string.Empty;

    public bool PersistOnServer { get; set; } = true;
}
