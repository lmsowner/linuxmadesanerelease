// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.SftpServer;

public sealed record SftpManagedUser(
    string UserName,
    SftpAuthenticationMode AuthenticationMode,
    bool IsEnabled,
    bool HasPassword,
    string LoginShell,
    string PrimaryGroup,
    IReadOnlyList<string> SupplementaryGroups,
    SftpUserFolder Folder,
    IReadOnlyList<SftpPublicKey> PublicKeys,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PasswordChangedAtUtc);
