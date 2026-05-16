// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ServiceActionKind
{
    Inspect = 0,
    Enable = 1,
    Disable = 2,
    Mask = 3,
    Unmask = 4,
    Start = 5,
    Stop = 6,
    Restart = 7
}
