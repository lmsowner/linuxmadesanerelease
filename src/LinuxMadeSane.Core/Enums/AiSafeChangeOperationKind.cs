// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum AiSafeChangeOperationKind
{
    GenericMutation = 0,
    ServiceRestart = 1,
    FileWrite = 2,
    PackageInstall = 3,
    Rollback = 4
}
