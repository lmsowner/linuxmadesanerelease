// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Application.Contracts.Shares;

public sealed record LocalUserAccessViewModel(
    string UserName,
    bool HasManagedPolicy,
    RemoteAccessSshAuthenticationMode? SshAuthenticationMode,
    bool HasAuthorizedKeys,
    DateTimeOffset? PasswordChangedAtUtc);
