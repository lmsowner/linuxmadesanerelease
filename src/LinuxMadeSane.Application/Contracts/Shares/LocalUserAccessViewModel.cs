// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record LocalUserAccessViewModel(
    string UserName,
    bool HasManagedPolicy,
    RemoteAccessSshAuthenticationMode? SshAuthenticationMode,
    bool HasAuthorizedKeys,
    DateTimeOffset? PasswordChangedAtUtc);
