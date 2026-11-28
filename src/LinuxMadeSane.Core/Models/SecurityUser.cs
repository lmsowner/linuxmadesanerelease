// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record SecurityUser(
    Guid Id,
    string Email,
    string LinuxUsername,
    bool IsEnabled,
    int SessionLifetimeMinutes,
    RemoteAccessSshAuthenticationMode SshAuthenticationMode,
    string AuthorizedKeyEntries,
    bool IsLocalAccountManaged,
    string OtpSecretReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? PasswordChangedAtUtc)
{
    // SSH policy stays with its original Linux account when the operator's association changes.
    // Null is the legacy target fallback; empty means this account does not manage SSH.
    public string? ManagedSshUsername { get; init; }
    public IReadOnlyDictionary<string, string> DomainOtpSecretReferences { get; init; } = new Dictionary<string, string>();
}
