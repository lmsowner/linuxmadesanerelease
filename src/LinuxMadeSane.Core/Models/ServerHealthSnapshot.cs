// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models;

public sealed record ServerHealthSnapshot(
    double? CpuUsagePercent,
    double? MemoryFreePercent,
    long? MemoryAvailableBytes,
    long? SwapFreeBytes,
    double? DiskUsagePercent,
    string RootDiskAvailable,
    string LoadAverage,
    string Uptime,
    DateTimeOffset CapturedAtUtc)
{
    public bool IsAvailable =>
        CpuUsagePercent.HasValue &&
        MemoryFreePercent.HasValue &&
        MemoryAvailableBytes.HasValue &&
        DiskUsagePercent.HasValue &&
        !string.IsNullOrWhiteSpace(RootDiskAvailable);
}
