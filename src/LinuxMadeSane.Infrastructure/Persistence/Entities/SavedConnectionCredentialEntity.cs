// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Application.Contracts;
namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SavedConnectionCredentialEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public ConnectionCredentialKind Kind { get; set; }
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string? PasswordReference { get; set; }
    public string? PrivateKeyReference { get; set; }
    public string? PassphraseReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
