// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ConnectionTestStatus
{
    NotRun = 0,
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    InvalidConfiguration = 4
}
