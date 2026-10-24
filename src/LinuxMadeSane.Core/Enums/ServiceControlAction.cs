// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ServiceControlAction
{
    Start = 0,
    Stop = 1,
    Restart = 2,
    EnableBoot = 3,
    DisableBoot = 4,
    Block = 5,
    Unblock = 6,
    Remove = 7,
    Restore = 8
}
