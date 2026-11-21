// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskRunbookOption(
    Guid Id,
    string Name,
    string Description,
    string CommandText,
    bool RequiresSudo,
    bool IsQuickAccess)
{
    public LinuxMadeSane.Core.Enums.RunbookTargetKind TargetKind { get; init; }

    public bool IsScript =>
        CommandText.Contains('\n') || CommandText.Contains('\r');
}
