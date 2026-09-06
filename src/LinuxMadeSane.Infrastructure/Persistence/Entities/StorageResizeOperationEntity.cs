// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class StorageResizeOperationEntity
{
    public Guid Id { get; set; }
    public int State { get; set; }
    public string DiskDevicePath { get; set; } = string.Empty;
    public string MountPoint { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public long BeforeSizeBytes { get; set; }
    public long AfterSizeBytes { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public bool BackupAcknowledged { get; set; }
    public string PlanJson { get; set; } = string.Empty;
    public string? BeforeTopologyJson { get; set; }
    public string? AfterTopologyJson { get; set; }
    public string? FailureDetail { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
}
