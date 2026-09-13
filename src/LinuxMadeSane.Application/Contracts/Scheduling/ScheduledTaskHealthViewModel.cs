// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Scheduling;

public sealed record ScheduledTaskHealthViewModel(
    bool CronDirectoryAvailable,
    bool CrontabBinaryAvailable,
    string DetectedServiceName,
    string ServiceState,
    string Summary);
