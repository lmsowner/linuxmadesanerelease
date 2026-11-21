// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
