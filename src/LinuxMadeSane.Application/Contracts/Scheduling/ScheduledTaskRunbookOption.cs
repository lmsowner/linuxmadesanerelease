// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskRunbookOption(
    Guid Id,
    string Name,
    string Description,
    string CommandText,
    bool RequiresSudo,
    bool IsQuickAccess)
{
    public bool IsScript =>
        CommandText.Contains('\n') || CommandText.Contains('\r');
}
