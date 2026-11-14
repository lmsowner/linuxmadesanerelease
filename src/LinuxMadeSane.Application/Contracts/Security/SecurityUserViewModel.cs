// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record SecurityUserViewModel(
    Guid Id,
    string Email,
    string LinuxUsername,
    bool IsEnabled,
    int SessionLifetimeMinutes,
    RemoteAccessSshAuthenticationMode SshAuthenticationMode,
    bool HasAuthorizedKeys,
    bool IsLocalAccountManaged,
    bool HasOtpSecret,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? PasswordChangedAtUtc);
