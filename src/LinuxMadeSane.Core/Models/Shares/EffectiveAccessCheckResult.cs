// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record EffectiveAccessCheckResult(
    string Principal,
    IReadOnlyList<string> GroupMembership,
    Guid ShareId,
    bool CanAccessShare,
    bool CanListFiles,
    bool CanCreateFile,
    bool CanDeleteOwnFile,
    bool CanDeleteOthersFiles,
    IReadOnlyList<string> Reasons);
