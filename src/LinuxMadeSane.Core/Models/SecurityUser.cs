// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
    DateTimeOffset? PasswordChangedAtUtc);
