// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class SecurityUserEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string LinuxUsername { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int SessionLifetimeMinutes { get; set; } = 720;
    public int SshAuthenticationMode { get; set; }
    public string AuthorizedKeyEntries { get; set; } = string.Empty;
    public bool IsLocalAccountManaged { get; set; }
    public string OtpSecretReference { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastLoginAtUtc { get; set; }
    public DateTimeOffset? PasswordChangedAtUtc { get; set; }
}
