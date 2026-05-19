// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Scheduling;

public sealed record ScheduledTaskLogSnapshot(
    Guid TaskId,
    string TaskName,
    string LogFilePath,
    bool Exists,
    string Content,
    DateTimeOffset? LastUpdatedAtUtc,
    bool IsTruncated);
