// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

namespace LinuxMadeSane.Core.Enums;

public enum ScheduledTaskScheduleMode
{
    Hourly = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    CustomCron = 4,
    Reboot = 5,
    Interval = 6
}
