// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts;

public sealed class UserManagedHostCredentialProfileEditor
{
    public Guid? Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ManagedHostId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool HasStoredPassword { get; set; }

    public bool ClearStoredPassword { get; set; }

    public string PrivateKey { get; set; } = string.Empty;

    public bool HasStoredPrivateKey { get; set; }

    public bool ClearStoredPrivateKey { get; set; }

    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    public bool HasStoredPrivateKeyPassphrase { get; set; }

    public bool ClearStoredPrivateKeyPassphrase { get; set; }
}
