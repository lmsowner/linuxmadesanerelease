// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ScheduledTaskKind
{
    ShellCommand = 0,
    FileCopy = 1,
    ShellScript = 2,
    SystemUpdate = 3,
    Runbook = 4,
    Cleanup = 5,
    HostBackup = 6,
    ServiceStart = 7,
    ServiceStop = 8,
    ServiceRestart = 9
}
