// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Enums;

public enum ScheduledTaskScheduleMode
{
    Hourly = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    CustomCron = 4,
    Reboot = 5
}
