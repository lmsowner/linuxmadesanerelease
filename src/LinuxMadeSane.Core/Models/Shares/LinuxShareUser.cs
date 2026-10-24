// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record LinuxShareUser(
    Guid Id,
    string UserName,
    int Uid,
    int Gid,
    string DisplayName,
    string PrimaryGroup,
    IReadOnlyList<string> SupplementaryGroups,
    string HomeDirectory,
    string LoginShell,
    bool IsEnabled)
{
    public LinuxMadeSane.Core.Enums.LocalUserSudoMode? SudoMode { get; init; }
}
