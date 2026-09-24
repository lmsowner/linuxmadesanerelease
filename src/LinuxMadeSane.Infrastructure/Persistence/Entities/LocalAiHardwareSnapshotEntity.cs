// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LocalAiHardwareSnapshotEntity
{
    public Guid Id { get; set; }
    public string CpuModel { get; set; } = string.Empty;
    public int PhysicalCoreCount { get; set; }
    public int LogicalCoreCount { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long AvailableMemoryBytes { get; set; }
    public long AvailableDiskBytes { get; set; }
    public int GpuAccelerationState { get; set; }
    public string GpusJson { get; set; } = "[]";
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; }
}
