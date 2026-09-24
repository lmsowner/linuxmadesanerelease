// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class UserManagedHostCredentialProfileEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ManagedHostId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? PasswordSecretReference { get; set; }
    public string? PrivateKeySecretReference { get; set; }
    public string? PrivateKeyPassphraseSecretReference { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
