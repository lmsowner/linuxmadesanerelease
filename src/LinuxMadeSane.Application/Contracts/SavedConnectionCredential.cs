// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using System.ComponentModel.DataAnnotations;

namespace LinuxMadeSane.Application.Contracts;

public enum ConnectionCredentialKind { Ssh, Sftp, Smb, SshKeyPair, Rdp, Vnc }

public sealed record SavedConnectionCredentialSummary(Guid Id, string Name, ConnectionCredentialKind Kind,
    string Server, int Port, string Username, string Domain, string PublicKey,
    bool HasPassword, bool HasPrivateKey, bool HasPassphrase, DateTimeOffset UpdatedAtUtc);

public sealed record SavedConnectionCredential(SavedConnectionCredentialSummary Summary,
    string Password, string PrivateKey, string Passphrase)
{
    public override string ToString() => $"Saved credential {Summary.Id} (secret values redacted)";
}

public sealed record SavedCredentialAudit(Guid? CredentialId, string Action, DateTimeOffset OccurredAtUtc);

public sealed class SavedConnectionCredentialEditor : IValidatableObject
{
    public bool UsesConnectionPort => Kind is not (ConnectionCredentialKind.Rdp or ConnectionCredentialKind.Vnc);
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UsesConnectionPort && Port is < 1 or > 65535)
            yield return new ValidationResult("Port must be between 1 and 65535.", [nameof(Port)]);
    }
    public Guid? Id { get; set; }
    [Required, StringLength(80)] public string Name { get; set; } = "";
    public ConnectionCredentialKind Kind { get; set; }
    [StringLength(255)] public string Server { get; set; } = "";
    public int Port { get; set; } = 22;
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
