// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed class RemoteShareMountEditor
{
    public Guid? ManagedMountId { get; set; }

    [Required]
    public string Target { get; set; } = string.Empty;

    public string RemoteAddress { get; set; } = string.Empty;

    [Required]
    public string ShareName { get; set; } = string.Empty;

    [Required]
    public string LocalMountPath { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool HasSavedPassword { get; set; }

    public bool KeepSavedPassword { get; set; }

    public string Domain { get; set; } = string.Empty;

    public string LocalOwner { get; set; } = string.Empty;

    public string FileMode { get; set; } = "0777";

    public string DirectoryMode { get; set; } = "0777";

    public bool PersistOnServer { get; set; }
}
