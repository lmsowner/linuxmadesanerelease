// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

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
    DateTimeOffset? PasswordChangedAtUtc)
{
    public SftpFilePermissions Permissions { get; init; } = SftpFilePermissions.All;
}
