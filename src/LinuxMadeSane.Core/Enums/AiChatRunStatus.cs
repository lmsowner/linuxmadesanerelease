// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Enums;

public enum AiChatRunStatus
{
    Queued = 0,
    Running = 1,
    AwaitingApproval = 2,
    Completed = 3,
    Failed = 4,
    CancellationRequested = 5,
    Cancelled = 6
}
