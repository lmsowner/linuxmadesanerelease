// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record LocalUserAccessPolicy(
    string UserName,
    bool IsManagedPolicy,
    RemoteAccessSshAuthenticationMode SshAuthenticationMode,
    string AuthorizedKeyEntries,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PasswordChangedAtUtc);
