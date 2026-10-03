// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts;

public enum ConnectionCredentialKind { Ssh, Sftp, Smb, SshKeyPair }

public sealed record SavedConnectionCredentialSummary(Guid Id, string Name, ConnectionCredentialKind Kind,
    string Server, int Port, string Username, string Domain, string PublicKey,
    bool HasPassword, bool HasPrivateKey, bool HasPassphrase, DateTimeOffset UpdatedAtUtc);

public sealed record SavedConnectionCredential(SavedConnectionCredentialSummary Summary,
    string Password, string PrivateKey, string Passphrase)
{
    public override string ToString() => $"Saved credential {Summary.Id} (secret values redacted)";
}

public sealed record SavedCredentialAudit(Guid? CredentialId, string Action, DateTimeOffset OccurredAtUtc);

public sealed class SavedConnectionCredentialEditor
{
    public Guid? Id { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    public ConnectionCredentialKind Kind { get; set; }
    [StringLength(255)] public string Server { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 22;
    [StringLength(128)] public string Username { get; set; } = "";
    [StringLength(128)] public string Domain { get; set; } = "";
    public string Password { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string Passphrase { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public bool ClearPassword { get; set; }
    public bool ClearPassphrase { get; set; }
    public bool HasPassword { get; set; }
    public bool HasPrivateKey { get; set; }
    public bool HasPassphrase { get; set; }
}
