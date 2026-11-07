// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class HostUpdateScheduleEntity
{
    public int Id { get; set; }

    public bool Enabled { get; set; }

    public int HourLocal { get; set; } = 3;

    public int MinuteLocal { get; set; }

    public bool ApplyPackageUpdates { get; set; } = true;

    public bool SecurityOnly { get; set; }

    public bool UseDistUpgrade { get; set; }

    public bool RebootIfRequired { get; set; }

    public DateTimeOffset? LastRunAtUtc { get; set; }

    public string? LastRunSummary { get; set; }

    public string? LastRunDayKey { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
