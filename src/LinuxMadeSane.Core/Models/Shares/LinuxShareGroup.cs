// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record LinuxShareGroup(
    Guid Id,
    string GroupName,
    int Gid,
    string Description,
    IReadOnlyList<string> Members);
