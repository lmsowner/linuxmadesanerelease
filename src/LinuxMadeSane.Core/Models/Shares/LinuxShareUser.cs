// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

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
    bool IsEnabled);
