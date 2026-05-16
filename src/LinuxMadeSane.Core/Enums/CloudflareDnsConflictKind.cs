// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum CloudflareDnsConflictKind
{
    None = 0,
    Reuse = 1,
    Update = 2,
    Conflict = 3
}
