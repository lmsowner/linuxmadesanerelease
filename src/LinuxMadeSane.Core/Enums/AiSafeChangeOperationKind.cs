// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Enums;

public enum AiSafeChangeOperationKind
{
    GenericMutation = 0,
    ServiceRestart = 1,
    FileWrite = 2,
    PackageInstall = 3,
    Rollback = 4
}
