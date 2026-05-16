// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models.Scheduling;

public sealed record ScheduledTaskHealthSnapshot(
    bool CronDirectoryAvailable,
    bool CrontabBinaryAvailable,
    string DetectedServiceName,
    string ServiceState,
    string Summary);
