// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ScheduledTaskKind
{
    ShellCommand = 0,
    FileCopy = 1,
    ShellScript = 2,
    SystemUpdate = 3,
    Runbook = 4,
    Cleanup = 5
}
