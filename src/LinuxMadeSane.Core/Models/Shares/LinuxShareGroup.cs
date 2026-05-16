// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Shares;

public sealed record LinuxShareGroup(
    Guid Id,
    string GroupName,
    int Gid,
    string Description,
    IReadOnlyList<string> Members);
